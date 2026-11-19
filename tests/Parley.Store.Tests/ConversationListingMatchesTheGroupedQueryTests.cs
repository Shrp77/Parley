using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the conversation listing, which reads the ends of an index per conversation, returns what the query that grouped every
/// message returned: the same conversations, in the same order, with the same message counts and activity, page by page.
/// </summary>
/// <remarks>
/// The reference below is the listing's SQL as it was before the Inbox stopped reading every message (0.62.3). It is kept as
/// text on purpose: the listing is an optimisation of it and must not drift from it. Stores are random, with conversations that
/// hold nothing, only a participant, only a move, or messages that share instants and are written in different offset forms.
/// </remarks>
[Integration]
public sealed class ConversationListingMatchesTheGroupedQueryTests : IDisposable
{
    private const string Reference =
        """
        WITH messaged AS (
            SELECT conversation_id AS id, count(*) AS messages, min(created_utc) AS first_utc, max(created_utc) AS last_utc
              FROM messages GROUP BY conversation_id
        ),
        activity AS (
            SELECT c.id AS id, coalesce(m.messages, 0) AS messages,
                   coalesce(m.first_utc, c.opened_utc) AS first_utc, coalesce(m.last_utc, c.opened_utc) AS last_utc
              FROM conversations c
              LEFT JOIN messaged m ON m.id = c.id
             WHERE m.id IS NOT NULL
                OR EXISTS (SELECT 1 FROM conversation_participants p WHERE p.conversation_id = c.id)
                OR EXISTS (SELECT 1 FROM shared_files f WHERE f.conversation_id = c.id)
                OR EXISTS (SELECT 1 FROM conversation_moves mv WHERE mv.source = c.id)
        )
        SELECT c.id AS Id, a.messages AS Messages, a.first_utc AS First, a.last_utc AS Last, (c.archived_utc IS NOT NULL) AS Archived
          FROM conversations c
          JOIN activity a ON a.id = c.id
         WHERE (@IncludeArchived OR c.archived_utc IS NULL)
           AND (@Participant IS NULL OR EXISTS (SELECT 1 FROM conversation_participants p WHERE p.conversation_id = c.id AND p.agent = @Participant))
           AND (@Since IS NULL OR a.last_utc >= @Since)
           AND (@Until IS NULL OR a.last_utc <= @Until)
           AND (@Before IS NULL OR a.last_utc < @Before OR (a.last_utc = @Before AND c.id < @BeforeId))
         ORDER BY a.last_utc DESC, c.id DESC
         LIMIT @Limit;
        """;

    private const string ReferenceArchived =
        """
        WITH messaged AS (
            SELECT conversation_id AS id, max(created_utc) AS last_utc FROM messages GROUP BY conversation_id
        ),
        activity AS (
            SELECT c.id AS id, coalesce(m.last_utc, c.opened_utc) AS last_utc
              FROM conversations c
              LEFT JOIN messaged m ON m.id = c.id
             WHERE m.id IS NOT NULL
                OR EXISTS (SELECT 1 FROM conversation_participants p WHERE p.conversation_id = c.id)
                OR EXISTS (SELECT 1 FROM shared_files f WHERE f.conversation_id = c.id)
                OR EXISTS (SELECT 1 FROM conversation_moves mv WHERE mv.source = c.id)
        )
        SELECT count(*) FROM conversations c JOIN activity a ON a.id = c.id
         WHERE c.archived_utc IS NOT NULL
           AND (@Participant IS NULL OR EXISTS (SELECT 1 FROM conversation_participants p WHERE p.conversation_id = c.id AND p.agent = @Participant))
           AND (@Since IS NULL OR a.last_utc >= @Since)
           AND (@Until IS NULL OR a.last_utc <= @Until);
        """;

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    private sealed class Ref
    {
        public string Id { get; set; } = "";
        public long Messages { get; set; }
        public string First { get; set; } = "";
        public string Last { get; set; } = "";
        public long Archived { get; set; }
    }

    private static string[] Instants() =>
    [
        "2026-03-01T10:00:00.0000000+00:00",
        "2026-03-01T10:00:00.0150000+00:00",
        "2026-03-01T10:00:00.0000001+00:00",
        "2026-03-01T10:00:01.0000000+00:00",
        "2026-03-02T09:30:00.0000000+00:00",
        "2026-03-03T00:00:00.0000000+00:00",
        "2026-03-05T23:59:59.9990000+00:00",
    ];

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void Seed(int seed)
    {
        var random = new Random(seed);
        var instants = Instants();

        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test', 'test', 'full', 'active'), ('billing@test', 'test', 'full', 'active')
                ON CONFLICT DO NOTHING;
            """);

        for (var i = 0; i < 24; i++)
        {
            var id = $"c{i:000}";
            var opened = instants[random.Next(instants.Length)];
            var archived = random.Next(4) == 0 ? "'2026-04-01T00:00:00.0000000+00:00'" : "NULL";
            _connection.Execute($"INSERT INTO conversations (id, state, opened_utc, archived_utc) VALUES ('{id}', 'OPEN', '{opened}', {archived});");

            switch (random.Next(5))
            {
                case 1:
                    _connection.Execute(
                        "INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES (@id, @agent, @opened);",
                        new { id, agent = random.Next(2) == 0 ? "orders@test" : "billing@test", opened });
                    break;
                case 2:
                    _connection.Execute(
                        "INSERT INTO conversation_moves (source, destination, moved_by, moved_utc, first_message, messages) VALUES (@id, 'elsewhere', 'human-user@test', @opened, NULL, 0);",
                        new { id, opened });
                    break;
                case 3:
                case 4:
                    _connection.Execute(
                        """
                        INSERT INTO exchanges
                            (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc, closed_utc, outcome,
                             uncollected_flag_ms, stalled_flag_ms, request_hash)
                        VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test', 'CLOSED',
                                @id, @opened, @opened, 'complete', 900000, 14400000, 'hash');
                        """,
                        new { id, opened });
                    var exchange = _connection.ExecuteScalar<long>("SELECT max(id) FROM exchanges;");

                    for (var m = random.Next(1, 6); m > 0; m--)
                    {
                        _connection.Execute(
                            """
                            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash, conversation_id, created_utc)
                            VALUES (@exchange, 'request', 'orders@test', 'billing@test', 'm', '{"ask":"x"}', @hash, @id, @at);
                            """,
                            new { exchange, id, hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), at = instants[random.Next(instants.Length)] });
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static string? Format(DateTimeOffset? at) => at?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private List<Ref> ReferencePages(string? participant, DateTimeOffset? since, DateTimeOffset? until, bool archived, int limit)
    {
        var all = new List<Ref>();
        string? before = null;
        var beforeId = "";

        while (true)
        {
            var page = _connection.Query<Ref>(
                Reference,
                new { Participant = participant, Since = Format(since), Until = Format(until), Before = before, BeforeId = beforeId, Limit = limit, IncludeArchived = archived }).ToList();

            if (page.Count == 0)
            {
                return all;
            }

            all.AddRange(page);
            before = Format(Parse(page[^1].Last));
            beforeId = page[^1].Id;
        }
    }

    [Fact]
    public async Task The_listing_returns_what_the_grouped_query_returned_over_random_stores()
    {
        var instants = Instants().Select(Parse).ToArray();
        var repository = new ConversationRepository(_connection);
        var compared = 0;

        for (var seed = 0; seed < 12; seed++)
        {
            _connection.Execute("DELETE FROM messages; DELETE FROM exchanges; DELETE FROM conversation_moves; DELETE FROM conversation_participants; DELETE FROM conversations;");
            Seed(seed);
            var random = new Random(1000 + seed);

            for (var round = 0; round < 8; round++)
            {
                var participant = random.Next(3) == 0 ? "orders@test" : null;
                DateTimeOffset? since = random.Next(3) == 0 ? instants[random.Next(instants.Length)] : null;
                DateTimeOffset? until = random.Next(3) == 0 ? instants[random.Next(instants.Length)] : null;
                var archived = random.Next(2) == 0;
                var limit = random.Next(1, 6);

                var expected = ReferencePages(participant, since, until, archived, limit);
                var actual = new List<ConversationSummary>();
                DateTimeOffset? before = null;
                string? beforeId = null;

                while (true)
                {
                    var page = await repository.ListAsync(
                        participant is null ? null : AgentName.Create(participant), since, until, limit, before, beforeId, archived,
                        TestContext.Current.CancellationToken);

                    if (page.Count == 0)
                    {
                        break;
                    }

                    actual.AddRange(page);
                    before = page[^1].LastActivityUtc;
                    beforeId = page[^1].Id;
                }

                Assert.Equal(
                    expected.Select(r => (r.Id, (int)r.Messages, Parse(r.First), Parse(r.Last), r.Archived == 1)),
                    actual.Select(s => (s.Id, s.Messages, s.FirstActivityUtc, s.LastActivityUtc, s.Archived)));

                var expectedArchived = _connection.ExecuteScalar<int>(
                    ReferenceArchived, new { Participant = participant, Since = Format(since), Until = Format(until) });
                var actualArchived = await repository.CountArchivedAsync(
                    participant is null ? null : AgentName.Create(participant), since, until, TestContext.Current.CancellationToken);

                Assert.Equal(expectedArchived, actualArchived);
                compared += expected.Count;
            }
        }

        Assert.True(compared > 200, $"only {compared} rows were compared");
    }
}
