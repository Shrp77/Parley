using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a store about to be migrated is first copied beside itself, as the previous build left it (SPEC.md §9.2).
/// </summary>
/// <remarks>
/// A live store holds work in flight and is upgraded in place; the copy is what a person restores if an upgrade has to
/// be undone. A new store, or one already current, is not copied, so a copy appears only when there is something to keep.
/// </remarks>
[Integration]
public sealed class MigrationBackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-backup").FullName;

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
    public void A_schema_one_store_is_copied_as_it_was_and_the_copy_is_named_for_the_schema_and_the_time()
    {
        var path = Path.Combine(_directory, "parley.db");
        SchemaOneStore(path);

        string? copy;

        using (var connection = ParleyDatabase.Open(path))
        {
            copy = ParleyDatabase.BackUpBeforeMigrating(connection, path, Now);
            SchemaMigrator.Migrate(connection);

            // Once current, nothing more is copied.
            Assert.Null(ParleyDatabase.BackUpBeforeMigrating(connection, path, Now.AddMinutes(1)));
        }

        Assert.Equal($"{path}.before-schema-{SchemaMigrator.LatestVersion}-20261027T093000Z", copy);

        using var kept = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False");
        kept.Open();

        Assert.Equal(1, SchemaMigrator.GetUserVersion(kept));
        Assert.Equal(1L, Scalar(kept, "SELECT count(*) FROM agents WHERE name = 'orders@test';"));
    }

    [Fact]
    public void A_new_store_is_not_copied()
    {
        var path = Path.Combine(_directory, "new.db");

        using var connection = ParleyDatabase.Open(path);

        Assert.Null(ParleyDatabase.BackUpBeforeMigrating(connection, path, Now));
        Assert.Empty(Directory.GetFiles(_directory, "new.db.before-*"));
    }

    private static void SchemaOneStore(string path)
    {
        using var connection = ParleyDatabase.Open(path);

        var assembly = typeof(SchemaMigrator).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".001_initial.sql", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        using var command = connection.CreateCommand();

        command.CommandText = reader.ReadToEnd() + """

            PRAGMA user_version = 1;
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00');
            INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test', 'claude-code', 'full', 'active');
            """;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), provider: null);
    }
}
