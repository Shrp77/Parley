using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The relationship two numbers must keep, which they did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is FR-006, and it exists because the defect was invisible by inspection.</b> Neither
/// number was wrong on its own. The broker waited five minutes, which is a reasonable time to hold
/// a caller; the node allowed twenty, which is a reasonable time to let a model work. They lived in
/// assemblies that cannot see each other, nothing compared them, and the gap between them was a
/// window in which every answer was destroyed.
/// </para>
/// <para>
/// So the comparison is a test rather than a comment. A future edit that shortens the sweep, or
/// lengthens a turn, fails here — which is the only place the two facts are in the same room.
/// </para>
/// </remarks>
public sealed class TurnBudgetTests
{
    [Fact]
    public void A_sweep_cannot_reach_a_turn_that_is_still_allowed_to_be_running()
    {
        // The invariant, and the one the old five-minute wait violated. Anything that settles an
        // instruction on age must wait longer than the machine running it will.
        Assert.True(
            TurnBudget.PresumedDead > TurnBudget.Longest,
            $"a sweep at {TurnBudget.PresumedDead} would settle a turn a node allows "
            + $"{TurnBudget.Longest} for, which is the defect this bound exists to prevent");
    }

    [Fact]
    public void The_margin_is_real_rather_than_nominal()
    {
        // A margin of one tick would satisfy the comparison above and prevent nothing: the two
        // clocks start at different moments, because the broker records an instruction before
        // delivery and the node starts counting when the process starts.
        Assert.True(
            TurnBudget.SweepMargin >= TimeSpan.FromMinutes(1),
            "the margin is too small to cover the delay between recording and starting");
    }

    [Fact]
    public void The_sweep_threshold_is_derived_rather_than_written()
    {
        // Not a tautology, though it reads like one. It fails the moment somebody replaces the
        // derivation with a literal -- which is exactly how the original two numbers came to
        // disagree, and the shape this type exists to refuse.
        Assert.Equal(TurnBudget.Longest + TurnBudget.SweepMargin, TurnBudget.PresumedDead);
    }

    [Fact]
    public void A_turn_is_allowed_long_enough_to_be_worth_waiting_for()
    {
        // Guards the other direction. Somebody shortening the turn budget to make a test run faster
        // would silently narrow what an agent may be asked to do, and the sweep would follow it
        // down.
        Assert.True(
            TurnBudget.Longest >= TimeSpan.FromMinutes(10),
            "a turn is a model working on a real repository, not a request");
    }
}
