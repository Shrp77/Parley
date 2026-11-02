using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>Runs alone: SQLite's temporary folder is one setting for the whole process.</summary>
[CollectionDefinition(nameof(SetsTheTemporaryFolder), DisableParallelization = true)]
public sealed class SetsTheTemporaryFolder;

/// <summary>That a rewrite refused for want of room still empties the log into the store (SPEC.md §9.1).</summary>
/// <remarks>
/// Found by review, lens 3 run 126: the fallback to a checkpoint was held by nothing. A rewrite refused for want of room
/// for its temporary copy (its folder gone, as a full disk would refuse it) must still copy the log's overwrite of a
/// deleted row into the store, so the store's own old page no longer holds it.
/// </remarks>
[Integration]
[Collection(nameof(SetsTheTemporaryFolder))]
public sealed class ARefusedRewriteStillEmptiesTheLogTests : IDisposable
{
    private const string Marker = "fallback-marker-5521";

    private readonly string _path = Path.Combine(Path.GetTempPath(), "parley-fallback-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // The temp folder takes it from here.
            }
        }
    }

    [Fact]
    public void A_rewrite_that_cannot_run_still_empties_the_log_into_the_store()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "parley-no-temp-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(temporary);

        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False;Default Timeout=1");
        connection.Open();
        SchemaMigrator.Configure(connection);

        Run(connection, "CREATE TABLE bulk (id INTEGER PRIMARY KEY, text TEXT NOT NULL);");
        Run(connection, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) "
            + "INSERT INTO bulk (text) SELECT printf('%.4000c', 'x') FROM n;");
        Run(connection, $"INSERT INTO bulk (text) VALUES ('{Marker}');");
        Run(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Assert.True(Holds(_path), "the marker never reached the store, so this test proves nothing");

        Run(connection, $"DELETE FROM bulk WHERE text = '{Marker}';");
        Assert.True(Holds(_path), "the delete reached the store before the flush, so this test proves nothing");

        try
        {
            // The rewrite's temporary copy has nowhere to go, and a two-page cache cannot hold it in memory instead.
            Run(connection, "PRAGMA cache_size = 2;");
            Run(connection, "PRAGMA temp_store = FILE;");
            Run(connection, $"PRAGMA temp_store_directory = '{temporary}';");
            Directory.Delete(temporary);

            // Unix SQLite skips a temporary folder that is gone and uses /var/tmp or /tmp instead (lens 3 run 127), so
            // the rewrite would run and this would prove nothing about the fallback there.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Skip("SQLite on this platform does not refuse a rewrite whose temporary folder is gone.");
            }

            ParleyDatabase.Flush(connection);
        }
        finally
        {
            Run(connection, "PRAGMA temp_store_directory = '';");
        }

        Assert.False(Holds(_path), "the deleted row is still in the store, so the log was not emptied into it");
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The test's own statements.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    private static bool Holds(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        return copy.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(Marker)) >= 0;
    }
}
