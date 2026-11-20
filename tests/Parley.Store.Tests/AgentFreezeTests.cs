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
    public async Task A_new_store_holds_nobody()
    {
        Assert.All(await _agents.GetAllAsync(Ct), agent => Assert.False(agent.Frozen));
    }

    [Fact]
    public async Task Freezing_is_read_back_by_a_second_reader_and_names_the_instant()
    {
        Assert.Equal(1, await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, Ct));

        var orders = await Read("orders@test");

        Assert.True(orders.Frozen);
        Assert.Equal(Then, orders.FrozenUtc);
        Assert.False((await Read("billing@test")).Frozen);
    }

    [Fact]
    public async Task Freezing_an_agent_already_frozen_changes_nothing_and_keeps_the_first_instant()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, Ct);

        Assert.Equal(0, await _agents.FreezeAsync(AgentName.Create("orders@test"), Then.AddHours(1), Ct));
        Assert.Equal(Then, (await Read("orders@test")).FrozenUtc);
    }

    [Fact]
    public async Task Unfreezing_clears_it_and_says_whether_there_was_one()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, Ct);

        Assert.Equal(1, await _agents.UnfreezeAsync(AgentName.Create("orders@test"), Ct));
        Assert.False((await Read("orders@test")).Frozen);
        Assert.Equal(0, await _agents.UnfreezeAsync(AgentName.Create("orders@test"), Ct));
    }

    [Fact]
    public async Task A_hold_does_not_touch_the_agents_status_or_its_registration()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, Ct);

        var orders = await Read("orders@test");

        Assert.Equal(AgentStatus.Active, orders.Status);
        Assert.Equal("test", orders.Adapter);
    }

    [Fact]
    public async Task Retiring_keeps_the_hold_so_a_reinstated_agent_is_still_frozen()
    {
        _ = await _agents.FreezeAsync(AgentName.Create("orders@test"), Then, Ct);
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

        Assert.Equal(0, await _agents.FreezeAsync(AgentName.Create($"{PersonSeat.Role}@proj"), Then, Ct));
        Assert.Equal(0, await _agents.FreezeAsync(AgentName.Create($"{Facilitation.Role}@proj"), Then, Ct));
    }

    [Fact]
    public void Storage_refuses_a_value_that_is_not_an_instant()
    {
        var refused = Assert.Throws<SqliteException>(() =>
            Execute("UPDATE agents SET frozen_utc = 'yesterday' WHERE name = 'orders@test';"));

        Assert.Contains("CHECK", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_hold_column_is_the_last_migration_and_changes_no_row()
    {
        Assert.True(SchemaMigrator.LatestVersion >= 11);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agents WHERE frozen_utc IS NOT NULL;";

        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }
}
