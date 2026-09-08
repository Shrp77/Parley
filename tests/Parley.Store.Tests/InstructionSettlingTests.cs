using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// What happens to an agent's reply when two settlements race, and one of them is wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>An instruction is settled from two places on purpose.</b> The operator's own call settles it
/// so that somebody who gave up waiting does not leave a row saying <c>pending</c> for ever, and
/// the node settles it when the agent has actually finished. Either may arrive first, and
/// <see cref="InstructionRepository.SettleAsync"/> takes the first — <c>WHERE outcome = 'pending'</c>.
/// </para>
/// <para>
/// First-wins is the right rule and these tests are not an argument against it. They pin what it
/// costs, because the cost is invisible: the loser is discarded silently, and the loser is the only
/// party that ever knows what the agent said.
/// </para>
/// </remarks>
public sealed class InstructionSettlingTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;

    public InstructionSettlingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _instructions = new InstructionRepository(_connection);

        using var command = _connection.CreateCommand();

        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) "
            + "VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) "
            + "VALUES ('orders@test','test','full','active');";

        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int minute) =>
        new(2026, 1, 1, 12, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_reply_that_arrives_after_the_broker_gave_up_is_discarded()
    {
        // **The loss, in the order it happens.** The broker waits five minutes and the node will
        // wait twenty, so a turn that takes longer than five is settled `failed` by the broker
        // while the agent is still working. When the agent finishes and the node reports what it
        // said, the row is no longer pending and the report is refused.
        var id = await _instructions.RecordAsync(Orders, "look at the retry policy", At(0), Ct);

        // The broker's patience runs out. Nothing has gone wrong; it simply stopped waiting.
        Assert.True(
            await _instructions.SettleAsync(id, InstructionOutcome.Failed, null, At(5), Ct),
            "the broker's own settlement should have taken the pending row");

        // The agent finishes and says what it did.
        var landed = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "I changed three files and the tests pass", At(12), Ct);

        Assert.False(landed, "the late report was accepted, so first-wins is not the rule here");

        var read = await _instructions.GetAsync(id, Ct);

        // What the store now says: the turn did not complete, and the agent said nothing. Both
        // are untrue, and there is no other copy of either.
        Assert.NotNull(read);
        Assert.Equal(InstructionOutcome.Failed, read!.Outcome);
        Assert.Null(read.Reply);
    }

    [Fact]
    public async Task A_reply_that_arrives_first_is_kept()
    {
        // The other order, and the one that works. Worth pinning beside the loss so the next
        // reader can see that the mechanism is sound and the timing is what is wrong.
        var id = await _instructions.RecordAsync(Orders, "look at the retry policy", At(0), Ct);

        Assert.True(
            await _instructions.SettleAsync(
                id, InstructionOutcome.Taken, "I changed three files", At(3), Ct),
            "the agent's own answer should have taken the pending row");

        Assert.False(
            await _instructions.SettleAsync(id, InstructionOutcome.Failed, null, At(5), Ct),
            "the broker's giving-up overwrote an answer that had already landed");

        var read = await _instructions.GetAsync(id, Ct);

        Assert.NotNull(read);
        Assert.Equal(InstructionOutcome.Taken, read!.Outcome);
        Assert.Equal("I changed three files", read.Reply);
    }

    [Fact]
    public async Task An_instruction_nobody_has_settled_reads_as_pending_with_nothing_said()
    {
        // The state the whole readback exists to be able to report. An agent that has not been
        // given the turn cannot have said anything, and the schema pairs the two.
        var id = await _instructions.RecordAsync(Orders, "look at the retry policy", At(0), Ct);

        var read = await _instructions.GetAsync(id, Ct);

        Assert.NotNull(read);
        Assert.Equal(InstructionOutcome.Pending, read!.Outcome);
        Assert.Null(read.Reply);
    }
}
