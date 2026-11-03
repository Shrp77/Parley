using Dapper;
using Microsoft.Data.Sqlite;
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
