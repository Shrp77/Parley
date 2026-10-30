using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That what destroy removes is gone from the store's file too, and that an upgraded store's earlier leftovers are wiped
/// once (SPEC.md §14, §9.2).
/// </summary>
/// <remarks>
/// Found by review, lens 2 run 96. SQLite leaves a deleted row's bytes in the file until a later write lands on them, so a
/// copy of the store (a backup, or one sent for diagnosis) still held the text of conversations destroyed, purged or
/// pruned.
/// </remarks>
[Integration]
public sealed class DeletedTextLeavesTheFileTests : IDisposable
{
    private const string Marker = "zebra-quartz-ledger-7731";

    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), "parley-wipe-" + Guid.NewGuid().ToString("N") + ".db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
                // A pooled connection may still hold it; the temp folder takes it from here.
            }
        }
    }

    [Fact]
    public async Task A_destroyed_conversations_text_is_not_left_in_the_file()
    {
        using (var connection = ParleyDatabase.OpenAndMigrate(_path))
        {
            Work.Register(connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));
            _ = await Work.OpenAsync(
                connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"), T0,
                Work.Request("is " + Marker + " gross or net?"), cancellationToken: Ct);
            Checkpoint(connection);
            Assert.True(Holds(_path), "the marker never reached the file, so this test proves nothing");

            Assert.Equal(
                DestroyVerdict.Gone,
                (await new ConversationRepository(connection).DestroyAsync(Work.Conversation, T0.AddMinutes(1), cancellationToken: Ct)).Verdict);
            Checkpoint(connection);
        }

        Assert.False(Holds(_path), "the destroyed text is still in the store's file");
        Assert.False(Holds(_path + "-wal"), "the destroyed text is still in the write-ahead log");
    }

    [Fact]
    public async Task The_write_ahead_log_does_not_keep_destroyed_text_past_its_end()
    {
        // The checkpoints SQLite runs by itself are passive and leave the log at full length; the next write starts it
        // again from the front, so without a size limit the frames that held the text stay past the new end.
        using var connection = ParleyDatabase.OpenAndMigrate(_path);
        Work.Register(connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));
        _ = await Work.OpenAsync(
            connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"), T0,
            Work.Request("is " + Marker + " gross or net?"), cancellationToken: Ct);
        Assert.True(Holds(_path + "-wal"), "the marker never reached the log, so this test proves nothing");

        _ = await new ConversationRepository(connection).DestroyAsync(Work.Conversation, T0.AddMinutes(1), cancellationToken: Ct);
        connection.Execute("PRAGMA wal_checkpoint(PASSIVE);");
        connection.Execute("UPDATE agents SET changed_utc = '2026-10-30T09:02:00.0000000Z' WHERE name = 'orders@test';");
        connection.Execute("PRAGMA wal_checkpoint(PASSIVE);");

        Assert.False(Holds(_path + "-wal"), "the destroyed text is still in the write-ahead log");
    }

    [Fact]
    public void Upgrading_a_store_wipes_text_deleted_before_deletes_were_secure()
    {
        using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            connection.Open();
            SchemaMigrator.Configure(connection);
            connection.Execute("PRAGMA secure_delete = OFF;");
            RunUpTo(connection, 3);

            connection.Execute(
                "INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-10-30T09:00:00.0000000Z');");
            connection.Execute(
                $"INSERT INTO agents (name, adapter, tier, status, session_ref) VALUES ('orders@test', 'claude-code', 'full', 'active', '{Marker}');");
            Checkpoint(connection);
            connection.Execute("DELETE FROM agents WHERE name = 'orders@test';");
            Checkpoint(connection);
        }

        Assert.True(Holds(_path), "an old build's delete left nothing behind, so this test proves nothing");

        using (var connection = ParleyDatabase.Open(_path))
        {
            Assert.Equal(SchemaMigrator.LatestVersion - 3, SchemaMigrator.Migrate(connection));
            Checkpoint(connection);
        }

        Assert.False(Holds(_path), "text deleted before the upgrade is still in the store's file");
    }

    [Fact]
    public void A_wipe_that_fails_leaves_the_store_at_its_old_schema_to_be_wiped_next_time()
    {
        // Lens 3 run 107: after the migrations, a failed wipe left the store upgraded, reported as not, and never wiped.
        using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            connection.Open();
            SchemaMigrator.Configure(connection);
            RunUpTo(connection, 3);
        }

        using (var connection = ParleyDatabase.Open(_path))
        {
            _ = Assert.ThrowsAny<SqliteException>(() =>
                SchemaMigrator.Migrate(connection, "VACUUM; SELECT * FROM zz_no_such_table;"));

            Assert.Equal(3, SchemaMigrator.GetUserVersion(connection));
            Assert.Equal(SchemaMigrator.LatestVersion - 3, SchemaMigrator.Migrate(connection));
        }
    }

    private static void RunUpTo(SqliteConnection connection, int version)
    {
        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal)
                     .Take(version))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            connection.Execute(reader.ReadToEnd());
        }

        connection.Execute(FormattableString.Invariant($"PRAGMA user_version = {version};"));
    }

    private static void Checkpoint(SqliteConnection connection) =>
        connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);");

    private static bool Holds(string file)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        return copy.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(Marker)) >= 0;
    }
}
