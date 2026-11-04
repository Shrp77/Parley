using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>That migration 006 upgrades a schema 5 store in place and touches no row it finds (SPEC.md §5.0, §9.2).</summary>
[Integration]
public sealed class MigrationSixTests : IDisposable
{
    private readonly SqliteConnection _connection = SchemaFive();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void It_adds_what_a_move_needs_and_every_table_it_found_is_as_it_was()
    {
        _connection.Execute(
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '2026-11-03T09:00:00Z');
            """);

        var before = Tables();
        var conversations = _connection.Query<string>("SELECT id || state || opened_utc FROM conversations;").ToList();

        SchemaMigrator.Migrate(_connection);

        Assert.Equal(SchemaMigrator.LatestVersion, _connection.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(conversations, _connection.Query<string>("SELECT id || state || opened_utc FROM conversations;"));
        Assert.Subset(Tables().ToHashSet(StringComparer.Ordinal), before.ToHashSet(StringComparer.Ordinal));

        foreach (var added in new[] { "conversation_links", "conversation_moves", "conversation_move_exchanges", "move_notices" })
        {
            Assert.Contains(added, Tables());
        }

        Assert.Equal(1, _connection.ExecuteScalar<int>(
            "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'tr_a_moved_exchange_may_go_there';"));
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public void Every_row_of_every_table_it_found_is_kept_as_it_was()
    {
        const string When = "2026-11-03T09:00:00.0000000Z";

        Work.Register(_connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));

        _connection.Execute(
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', @When);
            INSERT INTO conversations (id, state, opened_utc, closed_utc, closed_by) VALUES ('old-work', 'CLOSED', @When, @When, 'person');
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES
              ('pricing', 'orders@test', @When),
              ('pricing', 'billing@test', @When);
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test',
                    'billing@test', 'OPEN', 'pricing', @When, 900000, 14400000, 'hash-1');
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                                  conversation_id, created_utc, delivered_utc)
            SELECT id, 'request', 'orders@test', 'billing@test', 'test', json_object('ask', 'net?'),
                   '0000000000000000000000000000000000000000000000000000000000000001', 'pricing', @When, @When
              FROM exchanges;
            """,
            new { When });

        var before = Dump();

        SchemaMigrator.Migrate(_connection);

        var after = Dump();

        Assert.Contains("CLOSED", string.Concat(before["conversations"]), StringComparison.Ordinal);
        Assert.Contains("delivered_utc=" + When, Assert.Single(before["messages"]), StringComparison.Ordinal);

        foreach (var (table, rows) in before)
        {
            Assert.Equal(rows, after[table]);
        }
    }

    [Fact]
    public void A_conversation_cannot_be_its_own_parent_and_has_only_one()
    {
        SchemaMigrator.Migrate(_connection);

        void Link(string child, string parent) => _connection.Execute(
            "INSERT INTO conversation_links (child, parent, how, linked_utc) VALUES (@child, @parent, 'spinoff', '2026-11-03T09:00:00Z');",
            new { child, parent });

        Link("a", "b");

        Assert.Throws<SqliteException>(() => Link("a", "c"));
        Assert.Throws<SqliteException>(() => Link("d", "d"));
    }

    private Dictionary<string, string[]> Dump() => _connection
        .Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")
        .ToDictionary(
            table => table,
            table => _connection.Query($"SELECT * FROM \"{table}\" ORDER BY 1, 2;")
                .Select(row => string.Join('|', ((IDictionary<string, object>)row).Select(c => $"{c.Key}={c.Value}")))
                .ToArray());

    private List<string> Tables() =>
        [.. _connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;")];

    private static SqliteConnection SchemaFive()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON;");

        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var file in new[]
        {
            ".001_initial.sql", ".002_proposal_ids_and_item_openings.sql", ".003_file_problems.sql", ".004_asked_once.sql",
            ".005_soft_message_limit.sql",
        })
        {
            using var stream = assembly.GetManifestResourceStream(
                assembly.GetManifestResourceNames().Single(name => name.EndsWith(file, StringComparison.Ordinal)))!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();

            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }

        connection.Execute("PRAGMA user_version = 5;");

        return connection;
    }
}
