using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 002 failing at its last statements leaves the store as it was, and a later attempt succeeds
/// (SPEC.md §9.2: a migration is one transaction with the version bump).
/// </summary>
/// <remarks>
/// Probed in review round 36: the rebuild has dropped the old table and made temporary tables by the time it fails,
/// so this holds that all of it is undone and nothing is left on the connection to trouble the next attempt.
/// </remarks>
public sealed class AFailedMigrationLeavesTheStoreAsItWasTests : IDisposable
{
    private const string Schema =
        "SELECT group_concat(type || name || coalesce(sql,''), '|') FROM (SELECT * FROM sqlite_master ORDER BY name);";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-failed-migration").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception left) when (left is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_failed_migration_rolls_back_whole_and_a_later_one_succeeds_on_the_same_connection()
    {
        var path = Path.Combine(_directory, "parley.db");
        var schemaOne = SchemaOneSql();

        using (var setup = ParleyDatabase.Open(path))
        {
            Exec(setup, schemaOne + """

                PRAGMA user_version = 1;
                INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00');
                INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test', 'claude-code', 'full', 'active');
                INSERT INTO roadmap_proposals (id, proposer, title, size, state, created_utc) VALUES (7, 'orders@test', 'T', 3, 'waiting', '2026-01-01T00:00:00Z');
                INSERT INTO roadmap_proposal_items (proposal_id, position, text) VALUES (7, 1, 'a'), (7, 2, 'b');
                INSERT INTO roadmap_proposal_needs (proposal_id, position, needs_position) VALUES (7, 2, 1);
                """);
        }

        using var connection = ParleyDatabase.Open(path);
        var schemaBefore = Text(connection, Schema);

        // The trigger the migration drops near its end is not there, so the migration fails after the rebuild.
        Exec(connection, "DROP TRIGGER tr_a_reserved_conversation_opens_with_the_person;");
        var mutated = Text(connection, Schema);

        Assert.ThrowsAny<SqliteException>(() => SchemaMigrator.Migrate(connection));

        Assert.Equal(1, SchemaMigrator.GetUserVersion(connection));
        Assert.Equal(mutated, Text(connection, Schema));
        Assert.Equal("0", Text(connection, "SELECT count(*) FROM temp.sqlite_master;"));
        Assert.Equal("1", Text(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("2", Text(connection, "SELECT count(*) FROM roadmap_proposal_items;"));
        Assert.Equal("1", Text(connection, "SELECT count(*) FROM roadmap_proposal_needs;"));

        // Put the trigger back as schema 1 has it, and the same connection migrates.
        var start = schemaOne.IndexOf("CREATE TRIGGER tr_a_reserved_conversation_opens_with_the_person", StringComparison.Ordinal);
        var end = schemaOne.IndexOf("END;", start, StringComparison.Ordinal) + 4;
        Exec(connection, schemaOne[start..end]);
        Assert.Equal(schemaBefore, Text(connection, Schema));

        Assert.Equal(SchemaMigrator.LatestVersion - 1, SchemaMigrator.Migrate(connection));
        Assert.Equal("2", Text(connection, "SELECT count(*) FROM roadmap_proposal_items;"));
        Assert.Equal("1", Text(connection, "SELECT count(*) FROM roadmap_proposal_needs;"));
        Assert.Equal("7", Text(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'roadmap_proposals';"));
        Assert.Equal("0", Text(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal("ok", Text(connection, "PRAGMA integrity_check;"));
        Assert.Empty(StoreShape.Problems(connection));
    }

    private static string SchemaOneSql()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".001_initial.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), provider: null) ?? "";
    }
}
