using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a search written for the filters it was given finds, counts and pages what the one condition with a guard for every
/// absent filter found: the same messages, the same total, the same number left.
/// </summary>
/// <remarks>
/// The reference is the search's SQL as it was before a given agent or conversation was written plainly (0.62.4), kept as
/// text. Every combination of the five filters is tried against one store, from the first page and from a cursor.
/// </remarks>
[Integration]
public sealed class SearchFiltersMatchTheOneConditionTests : IDisposable
{
    private const string Guarded =
        """
        (@W1 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W1) > 0))
        AND (@W2 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W2) > 0))
        AND (@Agent IS NULL OR from_agent = @Agent OR to_agent = @Agent)
        AND (@Conversation IS NULL OR conversation_id = @Conversation)
        AND (@Type IS NULL OR type = @Type)
        AND (@Since IS NULL OR unixepoch(created_utc, 'subsec') >= unixepoch(@Since, 'subsec'))
        AND (@Until IS NULL OR unixepoch(created_utc, 'subsec') <= unixepoch(@Until, 'subsec'))
        """;

    private static readonly string[] Agents = ["orders@test", "billing@test", "inventory@test"];

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public SearchFiltersMatchTheOneConditionTests()
    {
        var random = new Random(7);
        var words = new[] { "alpha", "beta", "gamma", "Delta" };

        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'test', 'full', 'active'), ('billing@test', 'test', 'full', 'active'),
                ('inventory@test', 'test', 'full', 'active');
            """);

        for (var c = 0; c < 3; c++)
        {
            var id = $"c{c}";
            _connection.Execute("INSERT INTO conversations (id, state, opened_utc) VALUES (@id, 'OPEN', '2026-03-01T00:00:00.0000000+00:00');", new { id });
            _connection.Execute(
                """
                INSERT INTO exchanges
                    (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc, closed_utc, outcome,
                     uncollected_flag_ms, stalled_flag_ms, request_hash)
                VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test', 'CLOSED',
                        @id, '2026-03-01T00:00:00.0000000+00:00', '2026-03-01T00:00:00.0000000+00:00', 'complete', 900000, 14400000, 'hash');
                """,
                new { id });
            var exchange = _connection.ExecuteScalar<long>("SELECT max(id) FROM exchanges;");

            for (var m = 0; m < 40; m++)
            {
                var from = Agents[random.Next(3)];
                var to = Agents.Where(a => a != from).ElementAt(random.Next(2));
                var text = $"{words[random.Next(4)]} {words[random.Next(4)]}";
                var request = random.Next(2) == 0;
                _connection.Execute(
                    """
                    INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash, conversation_id, created_utc)
                    VALUES (@exchange, @type, @from, @to, 'm', @payload, @hash, @id, @at);
                    """,
                    new
                    {
                        exchange, id, from, to,
                        type = request ? "request" : "clarify",
                        payload = request ? $$"""{"ask":"{{text}}"}""" : $$"""{"text":"{{text}}"}""",
                        hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                        at = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes((c * 40) + m).ToString("O", CultureInfo.InvariantCulture),
                    });
            }
        }
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Every_combination_of_filters_finds_counts_and_pages_what_the_guarded_condition_did()
    {
        var repository = new HistoryRepository(_connection);
        var reader = AgentName.Create("orders@test");
        var since = new DateTimeOffset(2026, 3, 1, 0, 30, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 3, 1, 1, 30, 0, TimeSpan.Zero);
        var checks = 0;

        var combinations =
            from agent in new string?[] { null, "billing@test" }
            from conversation in new string?[] { null, "c1" }
            from type in new MessageType?[] { null, MessageType.Clarify }
            from lower in new DateTimeOffset?[] { null, since }
            from upper in new DateTimeOffset?[] { null, until }
            from cursor in new long[] { 0, 70 }
            from search in new[] { new[] { "alpha" }, ["alpha", "beta"] }
            select (agent, conversation, type, lower, upper, cursor, search);

        foreach (var (agent, conversation, type, lower, upper, cursor, search) in combinations)
        {
            var parameters = new
            {
                W1 = search[0], W2 = search.Length > 1 ? search[1] : null,
                Agent = agent, Conversation = conversation,
                Type = type is null ? null : "clarify",
                Since = lower?.ToString("O", CultureInfo.InvariantCulture),
                Until = upper?.ToString("O", CultureInfo.InvariantCulture),
                Cursor = cursor, Limit = HistoryLimits.PageMatches,
            };

            var ids = _connection.Query<long>(
                $"SELECT id FROM messages WHERE (@Cursor = 0 OR id < @Cursor) AND {Guarded} ORDER BY id DESC LIMIT @Limit;", parameters).ToList();
            var counts = _connection.QuerySingle<(int Total, int Remaining)>(
                $"SELECT COUNT(*), COALESCE(SUM(CASE WHEN @Cursor = 0 OR id < @Cursor THEN 1 ELSE 0 END), 0) FROM messages WHERE {Guarded};", parameters);
            var (total, remaining) = cursor == 0 && ids.Count < HistoryLimits.PageMatches ? (ids.Count, ids.Count) : counts;

            _connection.Execute("DELETE FROM history_reads;");
            var slice = await repository.SearchAsync(
                reader, search, string.Join(' ', search), new HistoryFilter(agent, conversation, type, lower, upper), cursor,
                DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

            var label = $"agent={agent} conversation={conversation} type={type} since={lower} until={upper} cursor={cursor} words={string.Join('+', search)}";
            Assert.True(ids.SequenceEqual(slice.Matches.Select(m => m.MessageId)), $"matches differ: {label}");
            Assert.True(Math.Max(0, remaining - ids.Count) == slice.NotShown, $"remaining differs: {label}");
            Assert.True(total == _connection.ExecuteScalar<int>("SELECT matches FROM history_reads WHERE kind = 'search';"), $"total differs: {label}");
            checks++;
        }

        Assert.Equal(2 * 2 * 2 * 2 * 2 * 2 * 2, checks);
    }
}
