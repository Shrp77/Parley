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
/// <b>Why it exists</b> (specs/024): a turn ended with a command refused for want of permission and nothing reached the
/// broker, so the person's screen read "working" for nearly three hours. The node had measured all of it — how long, how it
/// ended, what was refused — and kept it to its own log.
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
    /// <param name="turn">The turn.</param>
    /// <returns>What the turn came to.</returns>
    internal async Task<TurnOutcome> RunAsync(Parley.Core.AgentName agent, Func<Task<TurnOutcome>> turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var started = DateTimeOffset.UtcNow;

        await SendAsync(new Wire.TurnReport
        {
            Machine = machine,
            Agent = agent.Value,
            Started = new Wire.TurnStarted { StartedUtc = Stamp(started) },
        }).ConfigureAwait(false);

        TurnOutcome? outcome = null;

        try
        {
            outcome = await turn().ConfigureAwait(false);

            return outcome;
        }
        finally
        {
            await SendAsync(new Wire.TurnReport
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
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a turn through a reporter when there is one, and plainly when there is not.</summary>
    /// <param name="reporter">The reporter, or null.</param>
    /// <param name="resident">The agent, whose name is reported.</param>
    /// <param name="turn">The turn.</param>
    /// <returns>What the turn came to.</returns>
    internal static Task<TurnOutcome> Run(TurnReporter? reporter, ResidentAgent resident, Func<Task<TurnOutcome>> turn) =>
        reporter is null ? turn() : reporter.RunAsync(resident.Name, turn);

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
            var why = failure is RpcException rpc ? rpc.Status.Detail : failure.Message;

            await output.WriteLineAsync($"  {report.Agent}: could not report its turn to the broker — {why}")
                .ConfigureAwait(false);
        }
    }
#pragma warning restore CA1031
}
