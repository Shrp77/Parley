namespace Parley.Headless;

/// <summary>
/// What one turn of a resident agent cost, and how it ended.
/// </summary>
/// <param name="Completed">Whether a result arrived at all.</param>
/// <param name="IsError">Whether the agent itself reported the turn as failed.</param>
/// <param name="StopReason">Why the model stopped, as it reported it.</param>
/// <param name="Elapsed">Wall-clock time the fleet spent waiting.</param>
/// <param name="CostUsd">What the turn cost, as the runner reported it.</param>
/// <param name="ModelTurns">How many model turns were taken inside this one Parley turn.</param>
/// <param name="PermissionDenials">How many tool calls were refused for want of permission.</param>
/// <param name="TimedOut">Whether the fleet gave up waiting.</param>
/// <param name="ProcessDied">Whether the agent's process ended instead of answering.</param>
/// <param name="Reply">
/// What the agent said when its turn ended, held in memory and never written down.
/// </param>
/// <remarks>
/// <para>
/// Recorded rather than merely logged because this is where a fleet becomes expensive. SPEC.md
/// §17 question 1 settled a comparable question by measuring instead of guessing, and a resident
/// agent costs considerably more than a message: it holds a session open and takes a
/// billable turn every time a peer says anything to it.
/// </para>
/// <para>
/// <paramref name="PermissionDenials"/> earns its place separately. A resident agent runs with
/// nobody at the keyboard, so a tool call that needs approval is not paused for a human — it is
/// refused, and the agent answers anyway, worse. Counting refusals is the only way that shows up
/// as anything other than an agent that has mysteriously become unhelpful.
/// </para>
/// <para>
/// <paramref name="Reply"/> is the one field deliberately not recorded anywhere. The
/// <c>invocations</c> table has no column for it and must not gain one: what an agent says
/// travels as messages under §9.4's retention, and a second copy there would sit outside every
/// guard that governs the first. It is held in memory for the length of one turn because
/// somebody watching a fleet needs a way to see why an agent that ran achieved nothing, and
/// discarding it before anyone can look leaves only the fact that it happened.
/// </para>
/// </remarks>
public sealed record TurnOutcome(
    bool Completed,
    bool IsError,
    string? StopReason,
    TimeSpan Elapsed,
    double CostUsd,
    int ModelTurns,
    int PermissionDenials,
    bool TimedOut,
    bool ProcessDied,
    string? Reply = null)
{
    /// <summary>Gets whether the turn ran to a clean finish.</summary>
    public bool Succeeded => Completed && !IsError && !TimedOut && !ProcessDied;

    /// <summary>Describes the outcome in a few words, for a log line or a report row.</summary>
    /// <returns>Why the turn ended.</returns>
    public string Describe() =>
        ProcessDied ? "process died"
        : TimedOut ? "timed out"
        : !Completed ? "no result"
        : IsError ? "agent reported an error"
        : StopReason ?? "completed";
}
