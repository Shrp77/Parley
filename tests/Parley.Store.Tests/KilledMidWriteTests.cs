using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a confirm or a purge stopped before its commit leaves the store as it was, and can be done whole after.
/// </summary>
/// <remarks>
/// Kept from review round 18, where both held. The database and its write-ahead log are copied while the write is open,
/// with the cache made small so the write has spilled into the log, which is what a broker killed mid-write leaves.
/// </remarks>
public sealed class KilledMidWriteTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-killed-" + Guid.NewGuid().ToString("N"));

    public KilledMidWriteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirm_killed_before_its_commit_leaves_no_roadmap_reservation_or_notice()
    {
        var live = Path.Combine(_directory, "live.db");
        var dead = Path.Combine(_directory, "dead.db");

        using var connection = ParleyDatabase.OpenAndMigrate(live);
        await SeedAsync(connection);

        var roadmaps = new RoadmapRepository(connection);
        var (proposal, problem) = await roadmaps.ProposeAsync(Orders, null, null, Big("Billing export"), T0, cancellationToken: Ct);
        Assert.Null(problem);

        Exec(connection, "PRAGMA cache_size = 2;");
        var walBefore = WalLength(live);

        using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false))
        {
            var waiting = await roadmaps.GetProposalAsync(proposal!.Value, Ct);
            var made = await roadmaps.ConfirmAsync(waiting!.Id, waiting.Content, null, T0, transaction, Ct);
            Assert.True(made.Done, made.Problem);

            // The pages are on disk, uncommitted: the process dies here.
            Assert.True(WalLength(live) > walBefore, "nothing spilled, so the probe proves nothing");
            Snapshot(live, dead);
        }

        using var reopened = ParleyDatabase.OpenAndMigrate(dead);

        Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check;"));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM roadmaps;"));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM roadmap_items;"));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM roadmap_item_conversations;"));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM roadmap_notices;"));
        Assert.Equal("waiting", Scalar(reopened, "SELECT state FROM roadmap_proposals;"));

        // And it can be confirmed again, once, after the restart.
        var again = new RoadmapRepository(reopened);
        var proposalAgain = await again.GetProposalAsync(proposal.Value, Ct);
        var second = await again.ConfirmAsync(proposalAgain!.Id, proposalAgain.Content, null, T0, cancellationToken: Ct);
        Assert.True(second.Done, second.Problem);
        Assert.Equal(1L, Count(reopened, "SELECT count(*) FROM roadmaps WHERE number = 1;"));
    }

    [Fact]
    public async Task A_purge_killed_before_its_commit_leaves_the_agent_its_conversations_and_no_destroyed_record()
    {
        var live = Path.Combine(_directory, "live.db");
        var dead = Path.Combine(_directory, "dead.db");

        using var connection = ParleyDatabase.OpenAndMigrate(live);
        await SeedAsync(connection);

        var roadmaps = new RoadmapRepository(connection);
        var (proposal, _) = await roadmaps.ProposeAsync(Orders, null, null, Big("Billing export"), T0, cancellationToken: Ct);
        var waiting = await roadmaps.GetProposalAsync(proposal!.Value, Ct);
        var made = await roadmaps.ConfirmAsync(waiting!.Id, waiting.Content, null, T0, cancellationToken: Ct);
        Assert.True(made.Done, made.Problem);

        // Billing proposes too, so a notice, a proposal and its items cascade with it; and it took item 1's conversation.
        _ = await roadmaps.ProposeAsync(Billing, null, null, Big("Billing's own"), T0, cancellationToken: Ct);
        StartItemOne(connection);

        var before = Census(connection);
        Exec(connection, "PRAGMA cache_size = 2;");
        var walBefore = WalLength(live);

        using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false))
        {
            var outcome = await new AgentRepository(connection).PurgeAsync(Billing, T0, transaction, Ct);
            Assert.NotEmpty(outcome.Destroyed);

            Assert.True(WalLength(live) > walBefore, "nothing spilled, so the probe proves nothing");
            Snapshot(live, dead);
        }

        using var reopened = ParleyDatabase.OpenAndMigrate(dead);

        Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check;"));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(before, Census(reopened));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM destroyed_conversations;"));

        // The temporary tables lived in the dead connection only: a purge after the restart works whole.
        var after = await new AgentRepository(reopened).PurgeAsync(Billing, T0, cancellationToken: Ct);
        Assert.NotEmpty(after.Destroyed);
        Assert.Equal(1L, Count(reopened, "SELECT count(*) FROM destroyed_conversations WHERE id = 'rm1-1';"));
        Assert.Equal(1L, Count(reopened, "SELECT count(*) FROM roadmap_item_conversations WHERE conversation_id = 'rm1-1';"));
        Assert.Equal(0L, Count(reopened, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    private static string Census(SqliteConnection connection) =>
        string.Join(
            ";",
            new[]
            {
                "agents", "conversations", "conversation_participants", "exchanges", "messages", "roadmaps",
                "roadmap_items", "roadmap_item_conversations", "roadmap_proposals", "roadmap_proposal_items",
                "roadmap_notices", "destroyed_conversations",
            }.Select(t => $"{t}={Count(connection, $"SELECT count(*) FROM {t};")}"))
        + ";nulled=" + Count(connection, "SELECT count(*) FROM roadmap_items WHERE intended_agent IS NULL;");

    private static RoadmapContent Big(string title) =>
        new(title, [.. Enumerable.Range(1, 200).Select(i => new RoadmapItemDraft(null, $"Step {i} " + new string('x', 400), i % 2 == 0 ? Orders : Billing, []))]);

    private static async Task SeedAsync(SqliteConnection connection)
    {
        var agents = new AgentRepository(connection);

        foreach (var name in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), Ct);
        }

        Exec(connection, "INSERT INTO agents (name, adapter, tier, status) VALUES ('human-user@test', 'parley-console', 'pull', 'active') ON CONFLICT DO NOTHING;");
    }

    private static void StartItemOne(SqliteConnection connection)
    {
        const string Stamp = FacilitatorStore.Stamp;

        Exec(connection, $"""
            INSERT INTO conversations (id, state, opened_utc) VALUES ('rm1-1', 'OPEN', '{Stamp}');
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('rm1-1', 'human-user@test', '{Stamp}'), ('rm1-1', 'billing@test', '{Stamp}');
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('billing@test' || char(31) || 'human-user@test', 'human-user@test', 'billing@test', 'billing@test',
                    'OPEN', 'rm1-1', '{Stamp}', 900000, 14400000, 'hash-killed');
            """);

        var exchange = (long)Scalar(connection, "SELECT last_insert_rowid();")!;

        Exec(connection, $$"""
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc)
            VALUES ({{exchange}}, 'request', 'human-user@test', 'billing@test', 'person',
                    '{"ask":"Step 1"}', '{{Guid.NewGuid():N}}{{Guid.NewGuid():N}}', 'rm1-1', '{{Stamp}}');
            """);
    }

    private static long WalLength(string path) => File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;

    private static void Snapshot(string from, string to)
    {
        foreach (var suffix in new[] { string.Empty, "-wal" })
        {
            if (!File.Exists(from + suffix))
            {
                continue;
            }

            using var source = new FileStream(from + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = new FileStream(to + suffix, FileMode.Create, FileAccess.Write);
            source.CopyTo(target);
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long Count(SqliteConnection connection, string sql) => Convert.ToInt64(Scalar(connection, sql), provider: null);
}
