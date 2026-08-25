using Microsoft.Extensions.Logging;
using Parley.Headless;
using Parley.Store;

namespace Parley.Mcp;

/// <summary>
/// Starts idle agents that owe answers, from inside the sweep that already runs.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes SPEC.md §11.1's Headless tier real. Until it existed the tier was a string
/// in a column: every exchange needed a person to take a turn at both ends, because a Pull agent
/// acts only when its session does.
/// </para>
/// <para>
/// **It runs in the existing sweep rather than in a daemon, and that is a deliberate trade.**
/// The README says there is no daemon, no port and no service to supervise, and that a machine
/// which reboots comes back with zero Parley processes and loses nothing. A resident invoker
/// would have made that false. So invocation is driven by whichever agent servers happen to be
/// live: open any one repository and the others converse around it; close everything and nothing
/// runs, which is already how Parley behaves. The cost is honest and worth stating — with every
/// session closed, no answer is produced.
/// </para>
/// <para>
/// One agent's server starting another is not authority over it (principle 2.6). It decides
/// nothing about the work: the invoked agent reads its own inbox, under its own identity, and
/// answers or does not. What is delegated is a turn, not a judgement — and the permission came
/// from the invoked agent's own launch configuration, not from the caller.
/// </para>
/// </remarks>
public sealed class InvocationSweep(
    BrokerSession session,
    ServerOptions options,
    ILogger<SweepService> logger)
{
    /// <summary>
    /// Starts every idle agent that owes an answer and has consented to being started.
    /// </summary>
    /// <param name="stoppingToken">Cancels the pass and any process it is waiting on.</param>
    /// <returns>How many agents were started.</returns>
    /// <remarks>
    /// <para>
    /// Sequential rather than parallel, and that is the important choice. Each invocation is a
    /// whole agent session: parallelism would multiply the token spend of one sweep by the
    /// number of idle peers, and the work is not urgent — anything missed is picked up a minute
    /// later. A pass that starts three agents one after another is a pass an operator can watch
    /// happening; three at once is a bill.
    /// </para>
    /// <para>
    /// Candidates are read once at the start. An agent that becomes busy mid-pass is invoked
    /// anyway, which is harmless: it finds an inbox somebody else has already drained and stops.
    /// Re-reading between each would cost a query per agent to avoid a wasted process that the
    /// turn checks already make safe.
    /// </para>
    /// </remarks>
    public async Task<int> RunAsync(CancellationToken stoppingToken)
    {
        if (options.InvokeCommand is null)
        {
            return 0;
        }

        var command = InvocationCommand.Parse(options.InvokeCommand);
        var invoker = new HeadlessInvoker(TimeProvider.System);
        var started = 0;

        // The lease §14 uses to tell "recently heard from" apart from "recorded as live". An
        // agent inside it has a session that will drain its own inbox; starting a second copy
        // would put two processes behind one identity, and the turn check would refuse the
        // loser after both had spent tokens getting there.
        var lease = BrokerOptionsLease(options);

        var candidates = await session.UseConnectionAsync(
            (connection, now, ct) => new InvocationRepository(connection)
                .FindOwedAsync(now - lease, ct),
            stoppingToken).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Never itself. This server's own agent is live by definition -- it is running -- so
            // the lease should already exclude it, and this is the belt to that brace: a clock
            // skew or a long-blocked gate must not make a session start a second copy of itself.
            if (candidate.Agent == options.Agent)
            {
                continue;
            }

            var before = candidate.Owed;
            var startedUtc = DateTimeOffset.UtcNow;

            InvocationOutcome outcome;

            try
            {
                outcome = await invoker
                    .InvokeAsync(command, candidate.Workspace, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // A missing workspace, a command that is not on PATH, a machine refusing to
                // start processes. None of them are this agent's problem to solve and none
                // should stop the pass reaching the next peer, but all of them are silent
                // otherwise -- the exchange simply stays owed and nobody learns why.
                logger.LogError(
                    ex, "Could not start {Agent} in {Workspace}.",
                    candidate.Agent.Value, candidate.Workspace);

                continue;
            }

            await session.UseConnectionAsync(
                async (connection, _, ct) =>
                {
                    var repository = new InvocationRepository(connection);
                    var after = await repository
                        .CountOwedAsync(candidate.Agent, ct).ConfigureAwait(false);

                    await repository.RecordAsync(
                        candidate.Agent, before, after, startedUtc, outcome.Elapsed,
                        outcome.ExitCode, outcome.TimedOut, ct).ConfigureAwait(false);

                    logger.LogInformation(
                        "Started {Agent}: owed {Before}, now {After}, {Elapsed}ms, exit {Exit}{TimedOut}.",
                        candidate.Agent.Value, before, after,
                        (long)outcome.Elapsed.TotalMilliseconds, outcome.ExitCode,
                        outcome.TimedOut ? ", timed out" : string.Empty);

                    return 0;
                },
                stoppingToken).ConfigureAwait(false);

            // Counted and logged at information because somebody's tokens were spent. The
            // quiet-when-nothing-happened rule the sweep follows does not apply here: an
            // invocation is never nothing.
            started++;
        }

        return started;
    }

    /// <summary>The liveness lease this server is running under.</summary>
    /// <param name="options">What this server was launched with.</param>
    /// <returns>How long an agent stays trusted as live after it was last heard from.</returns>
    /// <remarks>
    /// Derived the same way <see cref="BrokerSession"/> derives it, because the two must agree:
    /// a sweep using a shorter lease than the broker would start agents the broker still counts
    /// as attached.
    /// </remarks>
    private static TimeSpan BrokerOptionsLease(ServerOptions options) =>
        options.TickInterval * 3 > Parley.Broker.BrokerOptions.DefaultLivenessLease
            ? options.TickInterval * 3
            : Parley.Broker.BrokerOptions.DefaultLivenessLease;
}
