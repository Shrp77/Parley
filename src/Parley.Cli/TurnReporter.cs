using System.Globalization;
using Grpc.Core;
using Parley.Headless;
using Wire = Parley.Transport;

namespace Parley.Cli;

/// <summary>Tells the broker when a turn this node gives begins and ends (SPEC.md §11.6).</summary>
/// <param name="nodes">The node's client.</param>
/// <param name="machine">This node's machine.</param>
/// <param name="output">Where a report that could not be sent is noted.</param>
/// <remarks>
/// <para>
/// <b>Why it exists</b>: when a turn ends with a command refused for want of permission and nothing reaches the
/// broker, the person's screen reads "working" for as long as nobody looks. The node knows how long the turn ran,
/// how it ended and what was refused, so it tells the broker.
/// </para>
/// <para>
/// <b>A report never fails the turn it describes.</b> Each is sent with a short deadline and any failure is noted and
/// dropped: a broker that is slow or gone must not hold up an agent's work or make a finished turn look like a failed one.
/// The end is reported even when the turn throws, so the broker is not left holding a turn that will never end.
/// </para>
/// </remarks>
internal sealed class TurnReporter(Wire.Nodes.NodesClient nodes, string machine, TextWriter output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>Runs a turn, reporting its start and its end.</summary>
    /// <param name="agent">The agent whose turn it is.</param>
    /// <param name="turn">The turn, handed what to call once it actually begins.</param>
    /// <returns>What the turn came to.</returns>
    /// <remarks>
    /// <b>The start is reported when the turn begins, not when it is asked for</b>: a turn queued behind another for the
    /// same agent would otherwise be reported as starting early, and its end — the start plus the time it ran — would
    /// land before the work it took, so a stop on a refused permission would never be flagged. A
    /// turn that never began, cancelled while it queued, is reported not at all.
    /// </remarks>
    internal async Task<TurnOutcome> RunAsync(
        Parley.Core.AgentName agent, Func<Func<Task>, Task<TurnOutcome>> turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        DateTimeOffset? began = null;

        async Task Began()
        {
            began = DateTimeOffset.UtcNow;

            await SendAsync(new Wire.TurnReport
            {
                Machine = machine,
                Agent = agent.Value,
                Started = new Wire.TurnStarted { StartedUtc = Stamp(began.Value) },
            }).ConfigureAwait(false);
        }

        TurnOutcome? outcome = null;

        try
        {
            outcome = await turn(Began).ConfigureAwait(false);

            return outcome;
        }
        finally
        {
            if (began is { } started)
            {
                await ReportEndAsync(agent, started, outcome).ConfigureAwait(false);
            }
        }
    }

    private Task ReportEndAsync(Parley.Core.AgentName agent, DateTimeOffset started, TurnOutcome? outcome) =>
        SendAsync(new Wire.TurnReport
        {
            Machine = machine,
            Agent = agent.Value,
            Ended = new Wire.TurnEnded
            {
                StartedUtc = Stamp(started),
                ElapsedMs = (long)(outcome?.Elapsed ?? (DateTimeOffset.UtcNow - started)).TotalMilliseconds,
                Clean = outcome?.Succeeded ?? false,
                TimedOut = outcome?.TimedOut ?? false,
                ProcessDied = outcome?.ProcessDied ?? false,
                PermissionDenials = outcome?.PermissionDenials ?? 0,
                CostUsd = outcome?.CostUsd ?? 0,
                ModelTurns = outcome?.ModelTurns ?? 0,
            },
        });

    /// <summary>Tells the broker this node is about to start an agent, and gives back what to start it with.</summary>
    /// <param name="agent">The agent about to be started.</param>
    /// <returns>The environment to add to the process: its launch token.</returns>
    /// <remarks>
    /// <b>Reported before the process starts</b>, so the broker knows the token when the adapter presents it; and, like a
    /// turn report, never allowed to stop the launch — a broker that did not hear it reads the agent as open outside
    /// Parley, which is honest about what it knows. A fresh token each launch, so a process that outlived a restart, or
    /// an editor started from a shell that inherited one, cannot pass for the new one.
    /// </remarks>
    internal async Task<IReadOnlyDictionary<string, string>> LaunchAsync(Parley.Core.AgentName agent)
    {
        var token = Guid.NewGuid().ToString("N");

#pragma warning disable CA1031 // A report must never stop a launch, whatever went wrong sending it.
        try
        {
            _ = await nodes.ReportLaunchAsync(
                    new Wire.LaunchReport { Machine = machine, Agent = agent.Value, Token = token },
                    deadline: DateTime.UtcNow.Add(Patience))
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            var why = failure is RpcException rpc
                ? rpc.Status.Detail is { Length: > 0 } detail ? detail : rpc.StatusCode.ToString()
                : failure.Message;

            await output.WriteLineAsync($"  {agent.Value}: could not tell the broker it was starting it — {why}")
                .ConfigureAwait(false);
        }
#pragma warning restore CA1031

        return new Dictionary<string, string> { [Parley.Core.LaunchToken.Variable] = token };
    }

    /// <summary>Runs a turn through a reporter when there is one, and plainly when there is not.</summary>
    /// <param name="reporter">The reporter, or null.</param>
    /// <param name="resident">The agent, whose name is reported.</param>
    /// <param name="turn">The turn, handed what to call once it actually begins.</param>
    /// <returns>What the turn came to.</returns>
    internal static Task<TurnOutcome> Run(
        TurnReporter? reporter, ResidentAgent resident, Func<Func<Task>, Task<TurnOutcome>> turn) =>
        reporter is null ? turn(static () => Task.CompletedTask) : reporter.RunAsync(resident.Name, turn);

    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

#pragma warning disable CA1031 // A report must never fail a turn, whatever went wrong sending it.
    private async Task SendAsync(Wire.TurnReport report)
    {
        try
        {
            _ = await nodes.ReportTurnAsync(report, deadline: DateTime.UtcNow.Add(Patience)).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            var why = failure is RpcException rpc
                ? rpc.Status.Detail is { Length: > 0 } detail ? detail : rpc.StatusCode.ToString()
                : failure.Message;

            await output.WriteLineAsync($"  {report.Agent}: could not report its turn to the broker — {why}")
                .ConfigureAwait(false);
        }
    }
#pragma warning restore CA1031
}
