using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Reading an instruction back, sweeping the ones nothing reported on, and ageing them out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The store held all of this and nothing could ask for any of it.</b> SPEC.md §10.2 promises
/// the reply "is kept beside the instruction rather than in the message log" — and it was, in a
/// table with a <c>GetAsync</c> no operator surface called and no listing at all. The only route to
/// an agent's answer was the return value of the call that issued the instruction.
/// </para>
/// <para>
/// The sweep and the retention are here because removing the broker's habit of settling a record
/// when it stopped waiting removed the one thing that guaranteed a record ever settled. Both
/// replacements have to be shown to reach a pending row, which is the row neither would reach if
/// age were measured from settlement.
/// </para>
/// </remarks>
public sealed class InstructionReadbackTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@elsewhere");

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;

    public InstructionReadbackTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _instructions = new InstructionRepository(_connection);

        using var command = _connection.CreateCommand();

        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) VALUES "
            + "('test', 1, '2026-01-01T00:00:00+00:00'), "
            + "('elsewhere', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) VALUES "
            + "('orders@test','test','full','active'),"
            + "('billing@elsewhere','test','full','active');";

        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int minute) =>
        new(2026, 1, 1, 12, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_instruction_is_read_back_whole()
    {
        var id = await _instructions.RecordAsync(Orders, "look at the retry policy", At(0), Ct);

        _ = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "I changed three files", At(3), Ct);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        // Including the text: it is the operator's own words to their own agent, and a reply with
        // no question beside it is half a transcript.
        Assert.Equal(id, read.Id);
        Assert.Equal(Orders, read.Agent);
        Assert.Equal("look at the retry policy", read.Text);
        Assert.Equal(InstructionOutcome.Taken, read.Outcome);
        Assert.Equal("I changed three files", read.Reply);
        Assert.Equal(At(0), read.IssuedUtc);
        Assert.Equal(At(3), read.SettledUtc);
        Assert.False(read.StillRunning);
    }

    [Fact]
    public async Task A_running_instruction_reads_as_running_rather_than_failed()
    {
        // The distinction the whole feature turns on. "Still working" and "did not complete" are
        // different facts and an operator acting on the wrong one issues the instruction again.
        var id = await _instructions.RecordAsync(Orders, "have a look at the cache", At(0), Ct);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.True(read.StillRunning);
        Assert.Equal(InstructionOutcome.Pending, read.Outcome);
        Assert.Null(read.Reply);
        Assert.Null(read.SettledUtc);
    }

    [Fact]
    public async Task The_listing_is_narrowed_by_agent()
    {
        _ = await _instructions.RecordAsync(Orders, "one", At(0), Ct);
        _ = await _instructions.RecordAsync(Billing, "two", At(1), Ct);

        var mine = await _instructions.ListAsync(Orders, cancellationToken: Ct);

        Assert.Equal(Orders, Assert.Single(mine).Agent);
    }

    [Fact]
    public async Task The_listing_is_newest_first_and_pages_without_repeating_or_skipping()
    {
        for (var at = 0; at < 5; at++)
        {
            _ = await _instructions.RecordAsync(Orders, $"number {at}", At(at), Ct);
        }

        var first = await _instructions.ListAsync(limit: 2, cancellationToken: Ct);
        var second = await _instructions.ListAsync(
            limit: 2, beforeId: first[^1].Id, cancellationToken: Ct);

        Assert.Equal(["number 4", "number 3"], first.Select(i => i.Text));
        Assert.Equal(["number 2", "number 1"], second.Select(i => i.Text));

        // The property paging exists for, asserted rather than eyeballed: no row appears twice and
        // none is stepped over.
        var seen = first.Concat(second).Select(i => i.Id).ToList();

        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_sweep_settles_a_turn_nothing_ever_reported_on()
    {
        var id = await _instructions.RecordAsync(Orders, "a turn that died", At(0), Ct);

        var settled = await _instructions.SweepAsync(At(30), At(31), Ct);

        Assert.Equal(1, settled);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Failed, read.Outcome);
        Assert.Equal(At(31), read.SettledUtc);
    }

    [Fact]
    public async Task A_sweep_leaves_a_turn_that_is_still_young_enough_to_be_running()
    {
        var id = await _instructions.RecordAsync(Orders, "a turn still going", At(20), Ct);

        Assert.Equal(0, await _instructions.SweepAsync(At(10), At(31), Ct));

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.True(read.StillRunning);
    }

    [Fact]
    public async Task A_sweep_cannot_disturb_a_settled_instruction()
    {
        // FR-016, and it is free rather than careful: the same `outcome = 'pending'` clause that
        // makes settlement first-come is what bounds the sweep. Asserted anyway, because it is the
        // property that makes the sweep safe to run at all, and a later rewrite of the statement
        // could lose it without anything else noticing.
        var id = await _instructions.RecordAsync(Orders, "answered long ago", At(0), Ct);

        _ = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "all done", At(1), Ct);

        Assert.Equal(0, await _instructions.SweepAsync(At(59), At(59), Ct));

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Taken, read.Outcome);
        Assert.Equal("all done", read.Reply);
        Assert.Equal(At(1), read.SettledUtc);
    }

    [Fact]
    public async Task A_departed_machine_settles_only_its_own_agents_turns()
    {
        var mine = await _instructions.RecordAsync(Orders, "on the machine that went", At(0), Ct);
        var theirs = await _instructions.RecordAsync(Billing, "on a machine still up", At(0), Ct);

        Assert.Equal(1, await _instructions.SettleForMachineAsync("test", At(5), Ct));

        var gone = Assert.Single(await _instructions.ListAsync(id: mine, cancellationToken: Ct));
        var living = Assert.Single(await _instructions.ListAsync(id: theirs, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Failed, gone.Outcome);
        Assert.True(living.StillRunning);
    }

    [Fact]
    public async Task A_machine_whose_name_ends_another_machines_name_sweeps_nothing_of_theirs()
    {
        // Probed rather than reasoned, because the obvious implementation is a suffix match and a
        // suffix match is wrong: `test` ends `bigtest`, so a node called `test` leaving would have
        // settled every turn on `bigtest` as failed while that machine was working perfectly.
        using var command = _connection.CreateCommand();

        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) "
            + "VALUES ('bigtest', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) "
            + "VALUES ('inventory@bigtest','test','full','active');";

        command.ExecuteNonQuery();

        var neighbour = await _instructions.RecordAsync(
            AgentName.Create("inventory@bigtest"), "not yours to settle", At(0), Ct);

        _ = await _instructions.SettleForMachineAsync("test", At(5), Ct);

        var read = Assert.Single(
            await _instructions.ListAsync(id: neighbour, cancellationToken: Ct));

        Assert.True(read.StillRunning, "a neighbouring machine's turn was settled by name overlap");
    }
}
