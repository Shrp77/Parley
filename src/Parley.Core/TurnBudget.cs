namespace Parley.Core;

/// <summary>
/// How long a turn may run, and how long after that an unanswered instruction may be presumed dead.
/// </summary>
/// <remarks>
/// <para>
/// <b>These two numbers lived in assemblies that cannot see each other, and drifted.</b> The broker
/// waited five minutes for an instruction's turn; the node that runs the turn allowed twenty. Every
/// turn longer than five minutes was therefore settled <c>failed</c> — with no reply — while the
/// agent was still working, and the agent's real answer was refused when it arrived, because
/// settlement is first-come. The work was done, and the record said it had failed and that nothing
/// had been said.
/// </para>
/// <para>
/// <b>Here because it is the only place both readers can reach.</b> The node's invoker lives in
/// <c>Parley.Headless</c> and the broker's service in <c>Parley.Server</c>; neither references the
/// other, and <c>Parley.Server</c> does not reference <c>Parley.Headless</c> at all — checked
/// before this was designed around. Both reach this assembly through the store. <see
/// cref="MessageLimits"/> is already here on exactly the same footing: a protocol bound that things
/// far apart must agree on, stated once.
/// </para>
/// <para>
/// <b>What this is not is how long anybody waits.</b> Since the broker stopped settling an
/// instruction because it had stopped waiting, its own wait decides nothing and can be as short as
/// is comfortable for a caller. This bounds the *turn*, which is a fact about the machine running
/// the agent — and, derived from it, the point past which an instruction nothing has reported on
/// can be presumed dead rather than slow.
/// </para>
/// </remarks>
public static class TurnBudget
{
    /// <summary>The longest one turn may run before the machine hosting it gives up.</summary>
    /// <remarks>
    /// Twenty minutes is what the node has always allowed, kept rather than chosen: this change is
    /// about two numbers agreeing, and moving the one that was never wrong would have made it a
    /// different change. A turn is a model working on a real repository, and the cases that reach
    /// this are the ones worth waiting for.
    /// </remarks>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long past <see cref="Longest"/> an unreported instruction is left alone before it is
    /// presumed dead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The margin exists because the two clocks are not the same clock. The node measures a turn
    /// from when it starts the process; the broker measures from when it recorded the instruction,
    /// which is earlier by however long delivery took. A sweep with no margin would reach a turn
    /// that is still inside its own budget, which is the defect this type exists to prevent,
    /// arriving one mechanism further along.
    /// </para>
    /// <para>
    /// Generous rather than tight, because the two failure modes are not symmetric: sweeping too
    /// early destroys an answer that was about to arrive, and sweeping too late leaves a row saying
    /// <c>pending</c> for a few more minutes.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan SweepMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How old an unsettled instruction must be before a sweep may settle it as failed.
    /// </summary>
    /// <remarks>
    /// Derived rather than written, which is the whole point of this type. A second literal here
    /// would be a second number to keep in step with <see cref="Longest"/>, and keeping two numbers
    /// in step by hand is what produced the defect.
    /// </remarks>
    public static TimeSpan PresumedDead => Longest + SweepMargin;
}
