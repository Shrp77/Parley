using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Instructions under the age cap of SPEC.md §9.4, which they were not under at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pruner's only deletion was from the message log.</b> Nothing ever removed an instruction,
/// so the one table holding a person's own words was the one table that only grew — in a design
/// whose §9.4 opens by saying an unbounded log is something it explicitly is not.
/// </para>
/// <para>
/// <b>The trap these tests exist to hold shut is the age column.</b> The schema pairs a settled
/// outcome with a settled instant, so a row still running has none. A retention rule measured from
/// settlement would compile, read correctly, remove settled rows on schedule — and never once reach
/// a row that nothing ever settled, which is precisely the set most in need of a bound. An
/// unbounded table can hide inside a retention policy that looks complete, and only a test that
/// prunes a *pending* row can tell the two apart.
/// </para>
/// </remarks>
public sealed class InstructionRetentionTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RetentionPolicy Policy =
        new(IdlePeriod: TimeSpan.FromDays(3), MaximumAge: TimeSpan.FromDays(30));

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;
    private readonly Pruner _pruner;

    public InstructionRetentionTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _instructions = new InstructionRepository(_connection);
        _pruner = new Pruner(_connection);

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

    [Fact]
    public async Task An_instruction_past_the_age_cap_is_removed()
    {
        var id = await _instructions.RecordAsync(Orders, "long ago", T0, Ct);

        _ = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "done", T0.AddMinutes(1), Ct);

        var result = await _pruner.PruneAsync(Policy, T0.AddDays(31), Ct);

        Assert.Equal(1, result.InstructionsRemoved);
        Assert.Empty(await _instructions.ListAsync(id: id, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_instruction_inside_the_age_cap_is_kept()
    {
        var id = await _instructions.RecordAsync(Orders, "recent", T0, Ct);

        var result = await _pruner.PruneAsync(Policy, T0.AddDays(29), Ct);

        Assert.Equal(0, result.InstructionsRemoved);
        Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_running_instruction_is_reached_by_the_cap()
    {
        // **The one that would fail if age were measured from settlement.** This row has no settled
        // instant at all — the schema guarantees it — so a rule reading `settled_utc` would compare
        // against null, match nothing, and leave this row for ever while every test above still
        // passed.
        var id = await _instructions.RecordAsync(Orders, "nothing ever answered this", T0, Ct);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.True(read.StillRunning, "the fixture is only worth anything while this row is open");
        Assert.Null(read.SettledUtc);

        var result = await _pruner.PruneAsync(Policy, T0.AddDays(31), Ct);

        Assert.Equal(1, result.InstructionsRemoved);
        Assert.Empty(await _instructions.ListAsync(id: id, cancellationToken: Ct));
    }

    [Fact]
    public async Task Age_is_measured_from_when_it_was_issued_and_not_from_when_it_settled()
    {
        // The direction the previous test cannot see. An instruction issued long ago and settled
        // yesterday is old; a rule reading settlement would call it new and keep it for another
        // month, which is the same unbounded table arriving more slowly.
        var id = await _instructions.RecordAsync(Orders, "issued long ago", T0, Ct);

        _ = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "answered only now", T0.AddDays(30), Ct);

        var result = await _pruner.PruneAsync(Policy, T0.AddDays(31), Ct);

        Assert.Equal(1, result.InstructionsRemoved);
    }

    [Fact]
    public async Task Pruning_instructions_does_not_disturb_the_message_log_accounting()
    {
        // The two logs are counted separately and must stay that way: a caller reading `Removed`
        // to mean "messages" would otherwise start seeing instructions in the number, and §9.4's
        // floors say nothing about instructions.
        var id = await _instructions.RecordAsync(Orders, "long ago", T0, Ct);

        _ = await _instructions.SettleAsync(id, InstructionOutcome.Taken, "done", T0.AddDays(1), Ct);

        var result = await _pruner.PruneAsync(Policy, T0.AddDays(31), Ct);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.InstructionsRemoved);
        Assert.True(result.RemovedAnything);
    }

    [Fact]
    public async Task A_pass_that_removes_nothing_says_so_on_both_counts()
    {
        _ = await _instructions.RecordAsync(Orders, "recent", T0, Ct);

        var result = await _pruner.PruneAsync(Policy, T0.AddDays(1), Ct);

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.InstructionsRemoved);
        Assert.False(result.RemovedAnything);
    }
}
