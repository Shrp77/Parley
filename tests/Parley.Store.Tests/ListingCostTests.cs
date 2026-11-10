using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>Walking every page of the conversation listing stays cheap as exchanges pile up.</summary>
/// <remarks>
/// <para>
/// The Inbox walks every page on each render, and every page holds the broker's one gate, so a listing that grows with
/// conversations times exchanges stalls every agent. A per-conversation lookup of the newest close, with nothing indexing
/// exchanges by conversation, took about 40 seconds for 2,000 conversations of ten exchanges (review, lens 1 round 19).
/// </para>
/// <para>
/// The bound is generous on purpose: the grouped pass walks this store in well under a second, the per-row lookup in
/// tens of seconds, so only the wrong shape fails it, not a slow machine.
/// </para>
/// </remarks>
[Integration]
public sealed class ListingCostTests : IDisposable
{
    private const int Conversations = 1500;
    private const int ExchangesEach = 10;

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public ListingCostTests()
    {
        Execute(
            $$"""
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00')
                ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'test', 'full', 'active'),
                ('billing@test', 'test', 'full', 'active');

            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < {{Conversations}})
            INSERT INTO conversations (id, state, opened_utc)
                SELECT printf('c%05d', i), 'OPEN', '2026-01-01T00:00:00.0000000+00:00' FROM n;

            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
                SELECT id, 'orders@test', '2026-01-01T00:00:00.0000000+00:00' FROM conversations;

            WITH RECURSIVE k(j) AS (SELECT 0 UNION ALL SELECT j + 1 FROM k WHERE j + 1 < {{ExchangesEach}})
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc, closed_utc, outcome,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
                SELECT 'billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test',
                       'CLOSED', c.id, '2026-01-01T00:00:00.0000000+00:00',
                       printf('2026-01-%02dT00:00:00.0000000+00:00', 2 + (k.j % 20)), 'complete',
                       900000, 14400000, 'hash'
                  FROM conversations c, k;
            """);
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Every_page_of_a_large_listing_is_walked_in_seconds_not_minutes()
    {
        var repository = new ConversationRepository(_connection);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var seen = 0;
        DateTimeOffset? before = null;
        string? beforeId = null;

        for (var page = 0; page < (Conversations / 50) + 2; page++)
        {
            var rows = await repository.ListAsync(
                AgentName.Create("orders@test"), limit: 50, beforeLastActivityUtc: before, beforeId: beforeId,
                cancellationToken: TestContext.Current.CancellationToken);

            if (rows.Count == 0)
            {
                break;
            }

            seen += rows.Count;
            Assert.All(rows, row => Assert.NotNull(row.LastClosedUtc));
            before = rows[^1].LastActivityUtc;
            beforeId = rows[^1].Id;
        }

        Assert.Equal(Conversations, seen);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"walking the listing took {timer.Elapsed}");
    }
}
