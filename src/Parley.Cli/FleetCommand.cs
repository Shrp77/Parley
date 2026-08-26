using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Headless;

namespace Parley.Cli;

/// <summary>
/// Keeps the agents resident, so they can converse without anyone taking a turn for them.
/// </summary>
/// <remarks>
/// <para>
/// An operator operation (SPEC.md §10.2) and deliberately not an agent one. It starts other
/// agents' processes, which is the clearest case of something that must never appear in a tool
/// set — an agent able to start its peers could spend their tokens, and principle 2.6 says no
/// agent has authority over another.
/// </para>
/// <para>
/// <b>It is a command, not a daemon.</b> The README's claim is that there is no service to
/// supervise and that a machine which reboots comes back with zero Parley processes and loses
/// nothing, and that stays true: this runs in a terminal for as long as somebody wants a fleet,
/// and everything it started stops when it does. What it is not is resident across reboots, and
/// it deliberately installs nothing that would make it so.
/// </para>
/// <para>
/// Three ways the fleet ends, matching the three ways an agent should stop. The operator ends it
/// here, with a signal, and every agent is told to quit. The operator withdraws one agent — by
/// retiring it, or by removing the invoke command from its launch configuration — and the next
/// pass tells that one to quit. Or the agent's own process ends, which the next pass notices and
/// does not fight.
/// </para>
/// </remarks>
public sealed class FleetCommand
{
    private readonly SqliteConnection _connection;
    private readonly TextWriter _output;

    /// <summary>Creates the command over an open database.</summary>
    /// <param name="connection">The open, migrated database.</param>
    /// <param name="output">Where to write.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public FleetCommand(SqliteConnection connection, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(output);

        _connection = connection;
        _output = output;
    }

    /// <summary>
    /// Runs the fleet until it is stopped.
    /// </summary>
    /// <param name="interval">How long to wait between passes.</param>
    /// <param name="cancellationToken">Ends the fleet and stops every agent.</param>
    /// <returns>Zero when it stopped cleanly.</returns>
    /// <remarks>
    /// The first pass runs immediately rather than after one interval. Somebody who has just
    /// typed this wants to see whether it works, and a minute of silence is indistinguishable
    /// from a fleet that is broken.
    /// </remarks>
    public async Task<int> UpAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // The same lease the servers derive, because the two must agree. A supervisor using a
        // shorter one would start agents their own sessions still count as attached.
        var lease = interval * 3 > BrokerOptions.DefaultLivenessLease
            ? interval * 3
            : BrokerOptions.DefaultLivenessLease;

        var fleet = new AgentFleet(_connection, TimeProvider.System, lease);

        // Disposal is the backstop, not the plan: the shutdown below runs with its own token so
        // agents are told to quit even when the fleet is ending because the operator cancelled.
        await using var scope = fleet.ConfigureAwait(false);

        await WriteAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Fleet up. Passing every {interval.TotalSeconds:0}s; agents idle for {lease.TotalMinutes:0}m are considered unattended.")).ConfigureAwait(false);

        await WriteAsync("Press Ctrl+C to stop the fleet and every agent it started.")
            .ConfigureAwait(false);

        await WriteAsync(string.Empty).ConfigureAwait(false);

        using var timer = new PeriodicTimer(interval);
        var quiet = 0;

        try
        {
            do
            {
                var actions = await fleet.TickAsync(cancellationToken).ConfigureAwait(false);

                if (actions.Count == 0)
                {
                    // Counted rather than printed. A pass that finds nothing is the normal case,
                    // and a line per minute saying so would bury the passes that did something.
                    quiet++;
                    continue;
                }

                if (quiet > 0)
                {
                    await WriteAsync(string.Create(
                        CultureInfo.InvariantCulture, $"  ({quiet} quiet passes)"))
                        .ConfigureAwait(false);

                    quiet = 0;
                }

                foreach (var action in actions)
                {
                    await WriteAsync(Describe(action)).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The operator stopping the fleet. Not a failure.
        }

        await WriteAsync(string.Empty).ConfigureAwait(false);
        await WriteAsync("Stopping agents...").ConfigureAwait(false);

        // Not the caller's token: it is already cancelled, and shutting the agents down is
        // exactly the work that has to happen after it. A stop that inherited the cancellation
        // would skip the polite close and leave the fleet to be killed.
        var stopped = await fleet
            .StopAllAsync(TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);

        await WriteAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Fleet down. {stopped} agent{(stopped == 1 ? string.Empty : "s")} stopped."))
            .ConfigureAwait(false);

        return 0;
    }

    /// <summary>Renders one action as a line an operator can read at a glance.</summary>
    /// <param name="action">What happened.</param>
    /// <returns>The line.</returns>
    private static string Describe(FleetAction action)
    {
        var agent = action.Agent.Value;

        if (action.Skipped is { } why)
        {
            return string.Create(CultureInfo.InvariantCulture, $"  {agent}: {why}");
        }

        var turn = action.Turn!;
        var lead = action.Started ? "started, then took a turn" : "took a turn";

        var progress = action.MadeProgress
            ? string.Create(CultureInfo.InvariantCulture, $"waiting {action.WaitingBefore} → {action.WaitingAfter}")
            : string.Create(CultureInfo.InvariantCulture, $"waiting {action.WaitingBefore}, unchanged");

        // Cost is shown on every line that has one, not just the expensive ones. A fleet is the
        // first thing in this project that spends money while nobody is watching it, and a
        // number that appears only once it is large is one nobody has learned to read.
        var cost = turn.CostUsd > 0
            ? string.Create(CultureInfo.InvariantCulture, $", ${turn.CostUsd:0.00}")
            : string.Empty;

        var denials = turn.PermissionDenials > 0
            ? string.Create(CultureInfo.InvariantCulture, $", {turn.PermissionDenials} permission denied")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"  {agent}: {lead} — {turn.Describe()}, {progress}, {turn.Elapsed.TotalSeconds:0}s{cost}{denials}");
    }

    private async Task WriteAsync(string line) =>
        await _output.WriteLineAsync(line).ConfigureAwait(false);
}
