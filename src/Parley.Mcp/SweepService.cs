using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Parley.Mcp;

/// <summary>
/// Runs the broker's sweep on a timer for as long as the server is up.
/// </summary>
/// <remarks>
/// <para>
/// The broker owns no clock, deliberately: scheduling belongs to the host, and one that read the
/// time itself could not be tested without waiting. That makes this the piece a deployment must
/// supply, and without it nothing in SPEC.md §6 or §14 ever happens — no exchange orphans when
/// its window closes, no orphan is ghosted when its grace period runs out, and no closure poll
/// concludes by silence. The protocol would appear to work and would quietly stop enforcing
/// every deadline in it.
/// </para>
/// <para>
/// Through the same gate as every tool call, because this is the one piece of work that arrives
/// on a different thread by construction. Leaving it outside would reintroduce the race the gate
/// exists to prevent, and would do so rarely — the worst frequency for a data race.
/// </para>
/// </remarks>
/// <param name="session">The broker to sweep.</param>
/// <param name="options">The launch configuration, for the interval.</param>
/// <param name="logger">Where to report what a sweep did.</param>
public sealed class SweepService(
    BrokerSession session,
    ServerOptions options,
    ILogger<SweepService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer rather than a loop with a delay: it does not drift, and it drops ticks
        // rather than queueing them if a sweep ever runs long, which is the behaviour that keeps
        // a slow database from turning into a backlog of sweeps all wanting the same gate.
        using var timer = new PeriodicTimer(options.TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var result = await session.RunAsync(
                    (broker, now, ct) => broker.TickAsync(now, ct),
                    stoppingToken).ConfigureAwait(false);

                // Silence when nothing happened. A sweep that finds nothing is the normal case
                // and logging it would bury the ones that matter.
                if (result.Exchanges.Orphaned > 0 || result.Exchanges.Ghosted > 0 ||
                    result.ConversationsConcluded > 0)
                {
                    logger.LogInformation(
                        "Sweep: {Orphaned} orphaned, {Ghosted} ghosted, {Concluded} conversations concluded.",
                        result.Exchanges.Orphaned, result.Exchanges.Ghosted,
                        result.ConversationsConcluded);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown. Not a failure, and rethrowing would make a clean stop look like one.
                break;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Swallowed deliberately, and this is the one place in the codebase where that
                // is right. An unhandled exception here would take down the host, and with it a
                // server whose seven tools were working perfectly well — trading every operation
                // the agent can still perform for a deadline that will be re-evaluated in a
                // minute anyway. The sweep is idempotent by construction: it recomputes what is
                // expired from stored state each time, so a missed pass costs latency and
                // nothing else.
                logger.LogError(ex, "Sweep failed; the next one will retry.");
            }
        }
    }
}
