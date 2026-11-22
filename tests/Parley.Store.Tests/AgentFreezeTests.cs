using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a person's freeze on an agent is one column on its row, durable, guarded, and refused for the two participants the broker provides.
/// </summary>
/// <remarks>
/// Read back through a second repository rather than trusting a return value, as <c>ArchiveTests</c> does: a method that
/// reported a freeze and wrote nothing is the failure this guards against.
/// </remarks>
[Integration]
public sealed class AgentFreezeTests : IDisposable
{
    private static readonly DateTimeOffset Then = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public AgentFreezeTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        Execute("""
            INSERT INTO nodes (machine, connected, first_seen_utc)
            VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test','test','full','active'),
                ('billing@test','test','full','active');
            """);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private async Task<AgentRecord> Read(string name) =>
        (await new AgentRepository(_connection).GetAllAsync(Ct)).Single(a => a.Name.Value == name);

    [Fact]
    public async Task A_new_store_freezes_nobody()
    {
        Assert.All(await _agents.GetAllAsync(Ct), agent => Assert.False(agent.Frozen));
    }

    [Fact]
    public async Task Freezing_is_read_back_by_a_second_reader_and_names_the_instant()
    {
        Assert.Equal(1, await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, cancellationToken: Ct));

        var orders = await Read("orders@test");

        Assert.True(orders.Frozen);
        Assert.Equal(Then, orders.FrozenUtc);
        Assert.False((await Read("billing@test")).Frozen);
    }

    [Fact]
    public async Task Freezing_an_agent_already_frozen_changes_nothing_and_keeps_the_first_instant()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, cancellationToken: Ct);

        Assert.Equal(0, await _agents.FreezeAsync(AgentName.Create("orders@test"), Then.AddHours(1), cancellationToken: Ct));
        Assert.Equal(Then, (await Read("orders@test")).FrozenUtc);
    }

    [Fact]
    public async Task Unfreezing_clears_it_and_says_whether_there_was_one()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, cancellationToken: Ct);

        Assert.Equal(1, await _agents.UnfreezeAsync(AgentName.Create("orders@test"), Ct));
        Assert.False((await Read("orders@test")).Frozen);
        Assert.Equal(0, await _agents.UnfreezeAsync(AgentName.Create("orders@test"), Ct));
    }

    [Fact]
    public async Task A_freeze_does_not_touch_the_agents_status_or_its_registration()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, cancellationToken: Ct);

        var orders = await Read("orders@test");

        Assert.Equal(AgentStatus.Active, orders.Status);
        Assert.Equal("test", orders.Adapter);
    }

    [Fact]
    public async Task Retiring_keeps_the_freeze_so_a_reinstated_agent_is_still_frozen()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, cancellationToken: Ct);
        _ = await _agents.RetireAsync(AgentName.Create("orders@test"), Ct);
        _ = await _agents.ReinstateAsync(AgentName.Create("orders@test"), Ct);

        Assert.True((await Read("orders@test")).Frozen);
    }

    [Fact]
    public async Task The_person_seat_and_the_facilitator_cannot_be_frozen()
    {
        Execute($"""
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('{PersonSeat.Role}@proj','{PersonSeat.Adapter}','full','active'),
                ('{Facilitation.Role}@proj','{Facilitation.Adapter}','full','active');
            """);

        Assert.Equal(0, await _agents.FreezeAsync(AgentName.Create($"{PersonSeat.Role}@proj"), Then, cancellationToken: Ct));
        Assert.Equal(0, await _agents.FreezeAsync(AgentName.Create($"{Facilitation.Role}@proj"), Then, cancellationToken: Ct));
    }

    [Fact]
    public void Storage_refuses_a_value_that_is_not_an_instant()
    {
        var refused = Assert.Throws<SqliteException>(() =>
            Execute("UPDATE agents SET frozen_utc = 'yesterday' WHERE name = 'orders@test';"));

        Assert.Contains("CHECK", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_freeze_column_is_the_last_migration_and_changes_no_row()
    {
        Assert.True(SchemaMigrator.LatestVersion >= 11);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agents WHERE frozen_utc IS NOT NULL;";

        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task A_later_run_lifts_a_freeze_an_earlier_run_made_and_leaves_one_this_run_made()
    {
        var orders = AgentName.Create("orders@test");
        var billing = AgentName.Create("billing@test");
        _ = await _agents.FreezeAsync(orders, Then, "11111111-1111-1111-1111-111111111111", cancellationToken: Ct);
        _ = await _agents.FreezeAsync(billing, Then, "22222222-2222-2222-2222-222222222222", cancellationToken: Ct);

        var lifted = await _agents.UnfreezeLapsedAsync("22222222-2222-2222-2222-222222222222", Ct);

        Assert.Equal([orders], lifted);
        Assert.False((await Read("orders@test")).Frozen);
        Assert.True((await Read("billing@test")).Frozen);
    }

    [Fact]
    public async Task A_freeze_kept_across_a_start_is_not_lifted_and_says_so_in_its_record()
    {
        var orders = AgentName.Create("orders@test");
        _ = await _agents.FreezeAsync(orders, Then, "11111111-1111-1111-1111-111111111111", keepFrozen: true, cancellationToken: Ct);

        Assert.Empty(await _agents.UnfreezeLapsedAsync("22222222-2222-2222-2222-222222222222", Ct));

        var row = await Read("orders@test");

        Assert.True(row.Frozen);
        Assert.True(row.KeepFrozen);
    }

    [Fact]
    public async Task Unfreezing_clears_the_run_and_the_choice_to_keep()
    {
        var orders = AgentName.Create("orders@test");
        _ = await _agents.FreezeAsync(orders, Then, "11111111-1111-1111-1111-111111111111", keepFrozen: true, cancellationToken: Ct);
        _ = await _agents.UnfreezeAsync(orders, Ct);

        var row = await Read("orders@test");

        Assert.False(row.Frozen);
        Assert.False(row.KeepFrozen);
        Assert.Equal(0L, ScalarCount("SELECT COUNT(*) FROM agents WHERE frozen_run IS NOT NULL OR keep_frozen <> 0;"));
    }

    [Fact]
    public void Storage_refuses_a_run_or_a_keep_on_an_agent_that_is_not_frozen()
    {
        Assert.Throws<SqliteException>(() =>
            Execute("UPDATE agents SET keep_frozen = 1 WHERE name = 'orders@test';"));
        Assert.Throws<SqliteException>(() =>
            Execute("UPDATE agents SET frozen_run = '11111111-1111-1111-1111-111111111111' WHERE name = 'orders@test';"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("11111111-1111-1111-1111-11111111111")]
    [InlineData("11111111-1111-1111-1111-1111111111111")]
    public void Storage_refuses_a_run_that_is_not_thirty_six_long_on_a_frozen_agent(string run)
    {
        // The agent is frozen in the same statement, so the only thing wrong is the run's length: the refusal below is that
        // guard's and not the one that wants a run to belong to a freeze.
        var refused = Assert.Throws<SqliteException>(() => Execute(
            $"UPDATE agents SET frozen_utc = '2026-11-20T12:00:00+00:00', frozen_run = '{run}' WHERE name = 'orders@test';"));

        Assert.Contains("CHECK", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, ScalarCount("SELECT COUNT(*) FROM agents WHERE frozen_utc IS NOT NULL;"));

        // And one that is thirty-six long is taken, so the statement was refused for its length alone.
        Execute(
            "UPDATE agents SET frozen_utc = '2026-11-20T12:00:00+00:00', frozen_run = '11111111-1111-1111-1111-111111111111' "
            + "WHERE name = 'orders@test';");

        Assert.Equal(1L, ScalarCount("SELECT COUNT(*) FROM agents WHERE frozen_run IS NOT NULL;"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void Storage_refuses_a_keep_that_is_neither_nought_nor_one_on_a_frozen_agent(int keep)
    {
        var refused = Assert.Throws<SqliteException>(() => Execute(
            $"UPDATE agents SET frozen_utc = '2026-11-20T12:00:00+00:00', keep_frozen = {keep} WHERE name = 'orders@test';"));

        Assert.Contains("CHECK", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, ScalarCount("SELECT COUNT(*) FROM agents WHERE frozen_utc IS NOT NULL;"));

        // And one is taken on the same frozen agent, so the statement was refused for the value alone.
        Execute("UPDATE agents SET frozen_utc = '2026-11-20T12:00:00+00:00', keep_frozen = 1 WHERE name = 'orders@test';");

        Assert.Equal(1L, ScalarCount("SELECT COUNT(*) FROM agents WHERE keep_frozen = 1;"));
    }

    private long ScalarCount(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        return (long)command.ExecuteScalar()!;
    }
}
