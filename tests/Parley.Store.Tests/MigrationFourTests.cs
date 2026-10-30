using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 004 upgrades a schema 3 store in place and marks every agent Parley already asked what it owns,
/// whether its question is still there or its conversation was destroyed (SPEC.md §9.2, §10.2).
/// </summary>
/// <remarks>
/// Without the mark from the question rows, every agent already asked would be asked again after the upgrade; without
/// it from the tombstones, so would one whose question's conversation a person destroyed.
/// </remarks>
[Integration]
public sealed class MigrationFourTests : IDisposable
{
    private readonly SqliteConnection _connection = SchemaThree();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void An_agent_already_asked_is_marked_and_one_never_asked_is_not()
    {
        Work.Register(
            _connection,
            AgentName.Create("orders@test"),
            AgentName.Create("billing@test"),
            AgentName.Create("inventory@test"),
            AgentName.Create("shipping@test"));
        _connection.Execute(
            """
            INSERT INTO agents (name, adapter, tier, status) VALUES ('parley-facilitator@test', 'parley-facilitator', 'pull', 'active');
            INSERT INTO conversations (id, state, opened_utc) VALUES ('owns-orders-20261028-090000', 'OPEN', '2026-10-28T09:00:00.0000000Z');
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES
              ('owns-orders-20261028-090000', 'parley-facilitator@test', '2026-10-28T09:00:00.0000000Z'),
              ('owns-orders-20261028-090000', 'orders@test', '2026-10-28T09:00:00.0000000Z');
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('orders@test' || char(31) || 'parley-facilitator@test', 'parley-facilitator@test', 'orders@test',
                    'orders@test', 'OPEN', 'owns-orders-20261028-090000', '2026-10-28T09:00:00.0000000Z',
                    900000, 14400000, 'hash-1');
            INSERT INTO ownership_questions (agent, conversation_id, exchange_id, state, asked_utc)
            SELECT 'orders@test', 'owns-orders-20261028-090000', id, 'asked', '2026-10-28T09:00:00.0000000Z' FROM exchanges;
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants) VALUES
              ('owns-billing-20261028-090000', '2026-10-28T10:00:00.0000000Z', 2, 'billing@test, parley-facilitator@test'),
              ('owns-orders-20261028-080000', '2026-10-28T10:00:00.0000000Z', 2, 'inventory@test, orders@test, parley-facilitator@test');
            """);

        Assert.Equal(SchemaMigrator.LatestVersion - 3, SchemaMigrator.Migrate(_connection));

        Assert.Empty(StoreShape.Problems(_connection));
        Assert.Equal(
            ["billing@test 2026-10-28T10:00:00.0000000Z", "orders@test 2026-10-28T09:00:00.0000000Z"],
            _connection.Query<string>(
                "SELECT name || ' ' || asked_utc FROM agents WHERE asked_utc IS NOT NULL ORDER BY name;").ToList());
    }

    private static SqliteConnection SchemaThree()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Configure(connection);

        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var file in new[] { ".001_initial.sql", ".002_proposal_ids_and_item_openings.sql", ".003_file_problems.sql" })
        {
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }

        connection.Execute("PRAGMA user_version = 3;");

        return connection;
    }
}
