using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// First-come settlement, and what it costs the loser.
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
/// <para>
/// <b>Which is why the broker must not settle an instruction because it stopped waiting.</b> That
/// was the defect, and it lived one layer up — the rule here then did its job perfectly on a record
/// that should still have been open, and the agent's answer arrived to find the door shut. The
/// repair is in <c>Parley.Server</c>; these tests stayed to say the rule came through it intact.
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
    public async Task A_settled_instruction_refuses_a_later_report()
    {
        // **This pins first-come settlement, which is correct, and it is NOT where the defect
        // was.** Written while the loss was being diagnosed, and named for the loss, which sent
        // the reader to the wrong layer: a late report *should* be refused, or an operator's
        // record would change under them after they had read it.
        //
        // What was wrong was upstream. The broker settled `failed` because it had stopped
        // waiting — a fact about the broker, not about the turn — and that settlement is what
        // made the agent's real answer late. The rule below did exactly its job on a record that
        // should never have been settled. `Parley.Server.Tests` is where the repair is checked;
        // this stays to say the rule survived it.
        var id = await _instructions.RecordAsync(Orders, "look at the retry policy", At(0), Ct);

        // The broker's patience runs out. Nothing has gone wrong; it simply stopped waiting.
        Assert.True(
            await _instructions.SettleAsync(id, InstructionOutcome.Failed, null, At(5), Ct),
            "the broker's own settlement should have taken the pending row");

        // The agent finishes and says what it did.
        var landed = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "I changed three files and the tests pass", At(12), Ct);

        Assert.False(landed, "the late report was accepted, so first-wins is not the rule here");

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        // The first settlement stands, whole. That is the property; whether the first settlement
        // deserved to happen is a question for whoever made it.
        Assert.Equal(InstructionOutcome.Failed, read.Outcome);
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

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Taken, read.Outcome);
        Assert.Equal("I changed three files", read.Reply);
    }

    [Fact]
    public async Task An_instruction_nobody_has_settled_reads_as_pending_with_nothing_said()
    {
        // The state the whole readback exists to be able to report. An agent that has not been
        // given the turn cannot have said anything, and the schema pairs the two.
        var id = await _instructions.RecordAsync(Orders, "look at the retry policy", At(0), Ct);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Pending, read.Outcome);
        Assert.Null(read.Reply);
    }
}
