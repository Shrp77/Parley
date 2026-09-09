using Parley.Core;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console tells an operator a running turn is running, rather than that it failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wording is the whole of what these check, and it is not cosmetic.</b> §10.2 defines
/// <c>failed</c> as a turn attempted and not completed. While the turn is still being attempted
/// that claim is untrue, and the operator's obvious response to reading it — issue the instruction
/// again — is the one thing §10.2 names as making the situation worse.
/// </para>
/// <para>
/// The branch these exercise was unreachable until this feature. The console has always had an arm
/// for a pending instruction; nothing could produce one, because the broker turned its own
/// impatience into <c>failed</c> and settled the record with it, so the agent's real answer was
/// refused when it arrived.
/// </para>
/// </remarks>
public sealed class InstructionAnswerTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_running_turn_is_a_state_and_not_a_fault()
    {
        var record = new InstructionRecord(
            1, Orders, "look at the retry policy", InstructionOutcome.Pending, null, T0, null);

        Assert.True(record.StillRunning);
        Assert.Null(record.Reply);
        Assert.Null(record.SettledUtc);
    }

    [Fact]
    public void A_settled_turn_is_not_running()
    {
        var record = new InstructionRecord(
            1, Orders, "look", InstructionOutcome.Taken, "done", T0, T0.AddMinutes(1));

        Assert.False(record.StillRunning);
    }

    [Theory]
    [InlineData(InstructionOutcome.Taken)]
    [InlineData(InstructionOutcome.Declined)]
    [InlineData(InstructionOutcome.Undeliverable)]
    [InlineData(InstructionOutcome.Failed)]
    public void Every_settled_outcome_reads_as_settled(InstructionOutcome outcome)
    {
        // Non-vacuity for the test above: `StillRunning` must be true for exactly one outcome, or
        // the page would show a settled instruction as running and nobody would collect its reply.
        var record = new InstructionRecord(
            1, Orders, "look", outcome, null, T0, T0.AddMinutes(1));

        Assert.False(record.StillRunning);
    }

    [Fact]
    public void Issuing_returns_an_identifier_to_come_back_to()
    {
        // An operator told the turn is still running and given no identifier has been told to look
        // again and not told where.
        var result = new InstructionResult(InstructionOutcome.Pending, null, 42);

        Assert.Equal(42, result.Id);
        Assert.False(result.ReachedTheAgent);
    }

    [Fact]
    public void A_decline_reached_the_agent_and_a_running_turn_has_not_yet()
    {
        // `ReachedTheAgent` is deliberately not named for success. A decline was considered and
        // refused, which is a different thing from one that never arrived -- and from one still
        // being considered.
        Assert.True(new InstructionResult(InstructionOutcome.Declined, "no", 1).ReachedTheAgent);
        Assert.False(new InstructionResult(InstructionOutcome.Pending, null, 1).ReachedTheAgent);
        Assert.False(
            new InstructionResult(InstructionOutcome.Undeliverable, null, 1).ReachedTheAgent);
    }
}
