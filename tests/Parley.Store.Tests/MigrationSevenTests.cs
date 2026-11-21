using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>That migration 007 lets an image be placed, and keeps every file a store holds (SPEC.md §9.5, §9.2).</summary>
[Integration]
public sealed class MigrationSevenTests : IDisposable
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000001";

    private readonly SqliteConnection _connection = SchemaSix();

    public void Dispose() => _connection.Dispose();

    private void Place(string name) => _connection.Execute(
        """
        INSERT INTO shared_files (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
        VALUES ('pricing', upper(@name), @name, 3, @Hash, NULL, '2026-11-05T09:00:00Z', '2026-11-05T09:00:00Z', 7);
        """,
        new { name, Hash });

    private void Folder() => _connection.Execute(
        """
        INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '2026-11-05T09:00:00Z');
        INSERT INTO conversation_folders (conversation_id, folder, created_utc) VALUES ('pricing', 'pricing', '2026-11-05T09:00:00.000Z');
        """);

    [Fact]
    public void Before_it_an_image_is_refused_and_after_it_every_kind_is_taken()
    {
        Folder();

        Assert.Throws<SqliteException>(() => Place("shot.png"));

        SchemaMigrator.Migrate(_connection);

        foreach (var name in new[] { "a.png", "b.PNG", "c.jpg", "d.jpeg", "e.gif", "f.webp", "g.md", "h.txt" })
        {
            Place(name);
        }

        Assert.Equal(8, _connection.ExecuteScalar<int>("SELECT count(*) FROM shared_files;"));
    }

    [Theory]
    [InlineData("drawing.svg")]
    [InlineData("tool.exe")]
    [InlineData("page.html")]
    [InlineData("shot.png.exe")]
    [InlineData(".png")]
    public void Anything_else_is_still_refused(string name)
    {
        Folder();
        SchemaMigrator.Migrate(_connection);

        Assert.Throws<SqliteException>(() => Place(name));
    }

    [Fact]
    public void Every_file_a_store_held_is_kept_as_it_was()
    {
        Folder();
        Place("notes.md");
        Place("PriceSnapshot.cs.txt");

        const string All = "SELECT conversation_id || name_key || name || size_bytes || sha256 || coalesce(placed_by, '') || placed_utc || changed_utc || modified_ticks FROM shared_files ORDER BY name;";

        var before = _connection.Query<string>(All).ToList();

        SchemaMigrator.Migrate(_connection);

        Assert.Equal(before, _connection.Query<string>(All));
        Assert.Equal(SchemaMigrator.LatestVersion, _connection.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public void The_tables_triggers_are_back_and_still_hold()
    {
        Folder();
        SchemaMigrator.Migrate(_connection);

        foreach (var trigger in new[] { "tr_placer_is_registered", "tr_shared_file_stays_put", "tr_shared_file_path_fits", "tr_shared_file_path_fits_on_rename" })
        {
            Assert.Equal(1, _connection.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name = @trigger;", new { trigger }));
        }

        Assert.Throws<SqliteException>(() => _connection.Execute(
            """
            INSERT INTO shared_files (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
            VALUES ('pricing', 'A.PNG', 'a.png', 3, @Hash, 'nobody@here', '2026-11-05T09:00:00Z', '2026-11-05T09:00:00Z', 7);
            """,
            new { Hash }));

        Place("a.png");

        Assert.Throws<SqliteException>(() => _connection.Execute("UPDATE shared_files SET placed_utc = 'x';"));
        Assert.Empty(StoreShape.Problems(_connection));
    }

    private static SqliteConnection SchemaSix()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON;");

        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var file in new[]
        {
            ".001_initial.sql", ".002_proposal_ids_and_item_openings.sql", ".003_file_problems.sql", ".004_asked_once.sql",
            ".005_soft_message_limit.sql", ".006_conversation_moves.sql",
        })
        {
            using var stream = assembly.GetManifestResourceStream(
                assembly.GetManifestResourceNames().Single(name => name.EndsWith(file, StringComparison.Ordinal)))!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();

            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }

        connection.Execute("PRAGMA user_version = 6;");

        return connection;
    }
}
