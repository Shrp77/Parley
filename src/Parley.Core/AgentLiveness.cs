namespace Parley.Core;

/// <summary>
/// Works out what an agent's liveness actually is, from when it was last heard from.
/// </summary>
/// <remarks>
/// <para>
/// Liveness is derived rather than stored, and that is the whole point of this type. A stored
/// status is a claim somebody has to remember to withdraw, and the one case where withdrawing
/// matters most — a session that is killed, crashes, or loses its machine — is exactly the case
/// where nothing is left running to withdraw it. The old design wrote <c>live</c> on attach and
/// again on every sweep, and had no writer at all for the transition out, so a dead session's
/// row read <c>live</c> for ever with a frozen timestamp beside it that only the supervisor
/// ever compared. Everything that read the registry believed it.
/// </para>
/// <para>
/// A timestamp cannot lie in that direction. <c>last_seen_utc</c> stops advancing the moment the
/// process stops, whether it stopped politely or not, so a reader comparing it against the clock
/// gets the truth without anything having to write the truth down.
/// </para>
/// <para>
/// <see cref="AgentStatus.Retired"/> is the exception and is stored, because it is a judgement
/// about the future rather than an observation of the past: no timestamp implies "this is not
/// coming back", and a derived rule would clear it the moment a folder reappeared.
/// </para>
/// </remarks>
public static class AgentLiveness
{
    /// <summary>
    /// How many sweeps an agent may miss before it stops counting as live.
    /// </summary>
    /// <remarks>
    /// Three rather than one, because a single missed sweep is ordinary. A sweep can be late for
    /// reasons that say nothing about the agent — a busy machine, a slow write against a
    /// contended database, a laptop that suspended for a moment — and a status that flickered on
    /// each of those would train everybody to ignore it. Three consecutive misses is the point
    /// at which "late" stops being the better explanation.
    /// </remarks>
    public const int SweepsBeforeDoubt = 3;

    /// <summary>
    /// How long silence lasts before an agent is not worth routing to.
    /// </summary>
    /// <remarks>
    /// Independent of the sweep interval on purpose. The doubt window scales with how often the
    /// agent should have checked in, because that is a fact about configuration; this is a fact
    /// about people — five minutes is roughly how long a peer will wait before the answer stops
    /// being useful, and shortening it with a faster sweep would make a busy deployment write
    /// off agents that a quiet one tolerates.
    /// </remarks>
    public static TimeSpan SilenceBeforeUnreachable { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The sweep cadence assumed when a caller does not know the deployment's own.
    /// </summary>
    /// <remarks>
    /// Every server reads <c>PARLEY_TICK_SECONDS</c> and most leave it alone, so a reader that
    /// cannot see one agent's launch environment — which is every reader, since the registry
    /// does not record it — is right far more often than not. It only affects the boundary
    /// between Live and NotResponding, never the five-minute one, so being wrong here delays a
    /// verdict rather than producing the wrong verdict.
    /// </remarks>
    public static TimeSpan DefaultSweepInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reports what an agent's status is now, at the usual sweep cadence.
    /// </summary>
    /// <param name="stored">The status held in the registry.</param>
    /// <param name="lastSeenUtc">When the agent last swept, or <see langword="null"/> if never.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>The status to report.</returns>
    public static AgentStatus Derive(
        AgentStatus stored,
        DateTimeOffset? lastSeenUtc,
        DateTimeOffset nowUtc) =>
        Derive(stored, lastSeenUtc, nowUtc, DefaultSweepInterval);

    /// <summary>
    /// Reports what an agent's status is now.
    /// </summary>
    /// <param name="stored">The status held in the registry.</param>
    /// <param name="lastSeenUtc">When the agent last swept, or <see langword="null"/> if never.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="sweepInterval">How often this deployment's agents sweep.</param>
    /// <returns>The status to report.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sweepInterval"/> is not positive.</exception>
    /// <remarks>
    /// Order matters and is not arbitrary. Retirement wins over everything, including a live
    /// heartbeat: an agent whose operator retired it while its session was still running is
    /// retired, and reporting it as live would be the control-that-does-not-hold §14 describes.
    /// Never-seen wins over the age rules next, because an agent with no timestamp has not been
    /// silent for five minutes — it has not started, which is a different thing and routable.
    /// </remarks>
    public static AgentStatus Derive(
        AgentStatus stored,
        DateTimeOffset? lastSeenUtc,
        DateTimeOffset nowUtc,
        TimeSpan sweepInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sweepInterval, TimeSpan.Zero);

        if (stored == AgentStatus.Retired)
        {
            return AgentStatus.Retired;
        }

        if (lastSeenUtc is not { } seen)
        {
            return AgentStatus.Offline;
        }

        var silence = nowUtc - seen;

        // A clock that has gone backwards, or a timestamp written by a machine slightly ahead of
        // this one, produces a negative silence. Treated as live rather than as an error: the
        // agent demonstrably wrote recently, which is the question being asked.
        if (silence <= sweepInterval * SweepsBeforeDoubt)
        {
            return AgentStatus.Live;
        }

        return silence <= SilenceBeforeUnreachable
            ? AgentStatus.NotResponding
            : AgentStatus.Unreachable;
    }
}
