using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a search finds a message said at exactly its <c>since</c> or <c>until</c> time, and that every one of eight words
/// must appear (SPEC.md §10.1: "at or after", "at or before", "every word").
/// </summary>
/// <remarks>
/// <see cref="SearchFiltersMatchTheOneConditionTests"/> compares the search with a copy of its own condition over random
/// text, and no matching message falls on either bound, so `&lt;` for `&lt;=` (and `&gt;` for `&gt;=`) passed it and every
/// other test; no test searched with eight words, so the eighth being ignored passed too.
/// </remarks>
[Integration]
public sealed class ASearchKeepsItsEdgesTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public ASearchKeepsItsEdgesTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'test', 'full', 'active'), ('billing@test', 'test', 'full', 'active');
            INSERT INTO conversations (id, state, opened_utc) VALUES ('c0', 'OPEN', '2026-03-01T00:00:00.0000000+00:00');
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc, closed_utc, outcome,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test', 'CLOSED',
                    'c0', '2026-03-01T00:00:00.0000000+00:00', '2026-03-01T00:00:00.0000000+00:00', 'complete', 900000, 14400000, 'hash');
            """);

        _connection.Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash, conversation_id, created_utc)
            VALUES ((SELECT max(id) FROM exchanges), 'request', 'orders@test', 'billing@test', 'm', @payload, @hash, 'c0', @at);
            """,
            new
            {
                payload = """{"ask":"one two three four five six seven eight"}""",
                hash = new string('a', 64),
                at = At.ToString("O", CultureInfo.InvariantCulture),
            });
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_message_said_at_the_since_time_is_found_and_one_said_before_it_is_not()
    {
        Assert.Equal(1, await FoundAsync(["one"], since: At));
        Assert.Equal(1, await FoundAsync(["one"], since: At.ToOffset(TimeSpan.FromHours(-5))));
        Assert.Equal(0, await FoundAsync(["one"], since: At.AddMilliseconds(1)));
    }

    [Fact]
    public async Task A_message_said_at_the_until_time_is_found_and_one_said_after_it_is_not()
    {
        Assert.Equal(1, await FoundAsync(["one"], until: At));
        Assert.Equal(1, await FoundAsync(["one"], until: At.ToOffset(TimeSpan.FromHours(9))));
        Assert.Equal(0, await FoundAsync(["one"], until: At.AddMilliseconds(-1)));
    }

    [Fact]
    public async Task Every_one_of_eight_words_must_appear()
    {
        string[] eight = ["one", "two", "three", "four", "five", "six", "seven", "eight"];

        Assert.Equal(HistoryRepository.MaxWords, eight.Length);
        Assert.Equal(1, await FoundAsync(eight));

        for (var i = 0; i < eight.Length; i++)
        {
            var missing = eight.ToArray();
            missing[i] = "nine";

            Assert.True(await FoundAsync(missing) == 0, $"found with word {i + 1} of 8 absent");
        }
    }

    private async Task<int> FoundAsync(string[] words, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        var slice = await new HistoryRepository(_connection).SearchAsync(
            AgentName.Create("orders@test"), words, string.Join(' ', words), new HistoryFilter(Since: since, Until: until), 0,
            DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        return slice.Matches.Count;
    }
}
