using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That the broker's own tick settles an instruction nothing came back for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes is between a method and its caller.</b> <c>SweepAsync</c> was probed
/// thoroughly at the store — what it settles, what it leaves, that it cannot disturb a settled row
/// — and nothing checked that <c>TickAsync</c> calls it. A sweep nobody runs is the same as no
/// sweep, and it is the only thing left that settles an instruction whose machine went away without
/// reporting.
/// </para>
/// <para>
/// That matters more since the gate removed the other mechanism. A departing node used to settle
/// its turns; it does not, because a stream ending is not a process dying. So this is now the whole
/// of the guarantee that nothing stays *still running* for ever, and it rests on one call.
/// </para>
/// </remarks>
public sealed class InstructionSweepOnTickTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;
    private readonly Broker _broker;

    public InstructionSweepOnTickTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _instructions = new InstructionRepository(_connection);
        _broker = new Broker(_connection);

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
    public async Task A_tick_past_the_budget_settles_an_instruction_nothing_reported()
    {
        var id = await _instructions.RecordAsync(Orders, "nobody came back", T0, Ct);

        await _broker.StartAsync(Orders, T0, Ct);

        var tick = await _broker.TickAsync(T0 + TurnBudget.PresumedDead + TimeSpan.FromMinutes(1), Ct);

        Assert.Equal(1, tick.InstructionsAbandoned);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Failed, read.Outcome);
        Assert.NotNull(read.SettledUtc);
    }

    [Fact]
    public async Task A_tick_inside_the_budget_leaves_a_turn_alone()
    {
        // The half that stops the sweep being the defect it replaced. A node is permitted the whole
        // of TurnBudget.Longest, so a tick during it must not decide the turn failed.
        var id = await _instructions.RecordAsync(Orders, "still working", T0, Ct);

        await _broker.StartAsync(Orders, T0, Ct);

        var tick = await _broker.TickAsync(T0 + TurnBudget.Longest, Ct);

        Assert.Equal(0, tick.InstructionsAbandoned);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.True(read.StillRunning);
    }

    [Fact]
    public async Task A_tick_leaves_an_instruction_that_was_already_answered()
    {
        // First-come settlement, reached through the tick rather than through the repository. The
        // clause that guarantees it is in the statement, so this is checking that the caller has
        // not found some way around it.
        var id = await _instructions.RecordAsync(Orders, "answered in time", T0, Ct);

        _ = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "all done", T0.AddMinutes(1), Ct);

        await _broker.StartAsync(Orders, T0, Ct);

        var tick = await _broker.TickAsync(T0.AddDays(1), Ct);

        Assert.Equal(0, tick.InstructionsAbandoned);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Taken, read.Outcome);
        Assert.Equal("all done", read.Reply);
    }

    [Fact]
    public async Task Two_ticks_settle_one_instruction_once()
    {
        // Ticks overlap under load. The second must find nothing rather than rewriting the first
        // one's timestamp, which is corruption visible only to somebody reconstructing a timeline.
        var id = await _instructions.RecordAsync(Orders, "nobody came back", T0, Ct);

        await _broker.StartAsync(Orders, T0, Ct);

        var first = await _broker.TickAsync(T0.AddDays(1), Ct);
        var second = await _broker.TickAsync(T0.AddDays(2), Ct);

        Assert.Equal(1, first.InstructionsAbandoned);
        Assert.Equal(0, second.InstructionsAbandoned);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(T0.AddDays(1), read.SettledUtc);
    }

    [Fact]
    public async Task A_tick_on_a_deployment_with_no_instructions_reports_none()
    {
        // The quietest case, which is most deployments most of the time. A sweep that counted
        // wrongly here would put a number in every tick's result that meant nothing.
        await _broker.StartAsync(Orders, T0, Ct);

        var tick = await _broker.TickAsync(T0.AddDays(1), Ct);

        Assert.Equal(0, tick.InstructionsAbandoned);
    }
}
