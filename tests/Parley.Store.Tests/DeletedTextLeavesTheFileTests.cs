using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That what destroy, purge and retention remove is gone from the store's file and its log as soon as they return, and
/// that an upgraded store's earlier leftovers are wiped once (SPEC.md §14, §9.1).
/// </summary>
/// <remarks>
/// <para>
/// Found by review, lens 2 runs 96 and 97. SQLite leaves a deleted row's bytes in the file until a later write lands on
/// them, so a copy of the store (a backup, or one sent for diagnosis) still held the text of conversations destroyed,
/// purged or pruned. Overwriting deletes was not enough alone: the overwrite lands in the log, and until a checkpoint the
/// store keeps the old page and the log the frames that first wrote the text.
/// </para>
/// <para>
/// <b>No act here is followed by a checkpoint of the test's own</b>, because the broker runs none: what the file holds
/// when the call returns is what a copy taken then would hold.
/// </para>
/// </remarks>
[Integration]
public sealed class DeletedTextLeavesTheFileTests : IDisposable
{
    private const string Marker = "zebra-quartz-ledger-7731";

    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

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
    public async Task A_destroyed_conversations_text_is_gone_from_the_file_when_destroy_returns()
    {
        using var connection = await WithTheMarkerAsync(checkpointed: true);

        Assert.Equal(
            DestroyVerdict.Gone,
            (await new ConversationRepository(connection).DestroyAsync(Work.Conversation, T0.AddMinutes(1), cancellationToken: Ct)).Verdict);

        AssertGone();
    }

    [Fact]
    public async Task Text_still_in_the_log_is_gone_from_it_when_destroy_returns()
    {
        using var connection = await WithTheMarkerAsync(checkpointed: false);
        Assert.True(Holds(_path + "-wal"), "the marker never reached the log, so this test proves nothing");

        _ = await new ConversationRepository(connection).DestroyAsync(Work.Conversation, T0.AddMinutes(1), cancellationToken: Ct);

        AssertGone();
    }

    [Fact]
    public async Task A_purged_agents_text_is_gone_from_the_file_when_purge_returns()
    {
        using var connection = await WithTheMarkerAsync(checkpointed: true);

        Assert.Equal(PurgeVerdict.Gone, (await new AgentRepository(connection).PurgeAsync(Billing, T0.AddMinutes(1), cancellationToken: Ct)).Verdict);

        AssertGone();
    }

    [Fact]
    public async Task Pruned_text_is_gone_from_the_file_when_the_retention_pass_returns()
    {
        using var connection = await WithTheMarkerAsync(checkpointed: true, closed: true);

        var pruned = await new Pruner(connection).PruneAsync(new RetentionPolicy(TimeSpan.FromDays(1), TimeSpan.FromDays(2)), T0.AddDays(30), Ct);
        Assert.True(pruned.Removed > 0, "nothing was pruned, so this test proves nothing");

        AssertGone();
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

        // Read while the connection is still open and with no checkpoint of the test's own: the rewrite lands in the
        // log, and the store keeps its old pages until the wipe's own checkpoint (lens 3 run 109).
        using var upgraded = ParleyDatabase.Open(_path);
        Assert.Equal(SchemaMigrator.LatestVersion - 3, SchemaMigrator.Migrate(upgraded));

        Assert.False(Holds(_path), "text deleted before the upgrade is still in the store's file");
        Assert.False(Holds(_path + "-wal"), "text deleted before the upgrade is still in the write-ahead log");
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

    /// <summary>A store holding one Request whose text is the marker, in the store's file or still only in its log.</summary>
    private async Task<SqliteConnection> WithTheMarkerAsync(bool checkpointed, bool closed = false)
    {
        var connection = ParleyDatabase.OpenAndMigrate(_path);
        Work.Register(connection, Orders, Billing);

        var (exchange, _) = await Work.OpenAsync(
            connection, Orders, Billing, T0, Work.Request("is " + Marker + " gross or net?"), cancellationToken: Ct);

        if (closed)
        {
            _ = await Work.CloseAsync(connection, exchange, T0.AddMinutes(1), cancellationToken: Ct);
        }

        if (checkpointed)
        {
            Checkpoint(connection);
            Assert.True(Holds(_path), "the marker never reached the file, so this test proves nothing");
        }

        return connection;
    }

    private void AssertGone()
    {
        Assert.False(Holds(_path), "the removed text is still in the store's file");
        Assert.False(Holds(_path + "-wal"), "the removed text is still in the write-ahead log");
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
