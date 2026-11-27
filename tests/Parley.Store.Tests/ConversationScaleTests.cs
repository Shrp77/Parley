using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// A year of traffic across twenty agents, walked page by page.
/// </summary>
/// <remarks>
/// <para>
/// The listing is what makes an export reachable at all, and both of its failure modes are
/// invisible on a fixture holding three conversations. Paging by offset over a log that only
/// grows shows one conversation twice and another never; ordering by anything but the key the
/// resume bound is compared against admits or excludes the row on the boundary. Neither produces
/// an error, and both produce a page that looks entirely reasonable.
/// </para>
/// <para>
/// So the assertion is not that it is fast. It is that walking every page yields every
/// conversation exactly once, in one order, with no row appearing twice and none missing — which
/// is the only property an operator paging through a year of history can check for themselves,
/// and cannot check at all once the history is long enough to matter.
/// </para>
/// </remarks>
[Integration]
public sealed class ConversationScaleTests : IDisposable
{
    private const int Agents = 20;
    private const int Conversations = 240;
    private const int PageSize = 25;

    private readonly SqliteConnection _connection;
    internal readonly ConversationRepository _conversations;
    private int _hashes;

    /// <summary>A payload the schema accepts, kept out of the interpolation.</summary>
    private const string Payload =
        @"{""ask"":""who owns the invoice total, and does it include tax?""}";

    public ConversationScaleTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _conversations = new ConversationRepository(_connection);

        Execute("""
            INSERT INTO nodes (machine, connected, first_seen_utc)
            VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            """);

        for (var i = 0; i < Agents; i++)
        {
            Execute(
                $"INSERT INTO agents (name, adapter, tier, status) "
                + $"VALUES ('{Agent(i)}','test','full','active');");
        }

        // Twelve months, spread evenly, two agents per conversation and never the same pair twice
        // in a row. The dates matter more than the volume: the resume bound is a timestamp, so a
        // year of them is what exercises it.
        for (var i = 0; i < Conversations; i++)
        {
            Seed($"c{i:D4}", Agent(i % Agents), Agent((i + 7) % Agents), At(i));
        }
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Agent(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"agent{index:D2}@test");

    /// <summary>One instant per conversation, spread across a year.</summary>
    private static DateTimeOffset At(int index) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(index * 36);

    private static string Stamp(DateTimeOffset when) =>
        when.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The pair key exactly as the schema derives it.</summary>
    private static string Pair(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? $"{a}{b}" : $"{b}{a}";

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? ExecuteScalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    internal void Seed(string id, string asker, string answerer, DateTimeOffset when)
    {
        Execute($"""
            INSERT INTO conversations (id, state, opened_utc, closed_utc)
            VALUES ('{id}', 'OPEN', '{Stamp(when)}', NULL) ON CONFLICT DO NOTHING;

            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('{id}', '{asker}', '{Stamp(when)}'),
                   ('{id}', '{answerer}', '{Stamp(when)}')
            ON CONFLICT DO NOTHING;

            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 closed_utc, clarify_depth, uncollected_flag_ms, stalled_flag_ms, request_hash, outcome)
            VALUES ('{Pair(asker, answerer)}', '{asker}', '{answerer}', '{answerer}', 'CLOSED',
                    '{id}', '{Stamp(when)}',
                    '{Stamp(when.AddMinutes(1))}', 0, 900000, 14400000,
                    'request-hash-{id}', 'complete');
            """);

        var exchange = (long)(ExecuteScalar("SELECT last_insert_rowid();") ?? 0L);

        for (var i = 0; i < 3; i++)
        {
            _hashes++;

            Execute($"""
                INSERT INTO messages
                    (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                     content_hash, conversation_id, created_utc)
                VALUES ({exchange}, 'request', '{asker}', '{answerer}', 'some-model-9',
                        '{Payload}', '{_hashes:x64}', '{id}',
                        '{Stamp(when.AddMinutes(i))}');
                """);
        }
    }

    /// <summary>Walks every page, following the token the previous one returned.</summary>
    private async Task<List<string>> WalkAsync()
    {
        var seen = new List<string>();
        DateTimeOffset? token = null;

        // Bounded so a paging bug that never terminates fails as a failure rather than as a hung
        // run. A token that stops advancing is exactly the defect this walk exists to catch.
        for (var page = 0; page <= (Conversations / PageSize) + 2; page++)
        {
            var rows = await _conversations
                .ListAsync(limit: PageSize, beforeLastActivityUtc: token, cancellationToken: Ct)
                .ConfigureAwait(false);

            if (rows.Count == 0)
            {
                return seen;
            }

            seen.AddRange(rows.Select(r => r.Id));
            token = rows[^1].LastActivityUtc;
        }

        Assert.Fail("paging did not terminate: the resume token stopped advancing.");

        return seen;
    }

    [Fact]
    public async Task Every_conversation_appears_exactly_once_across_every_page()
    {
        // The two failures at once. A duplicate means the resume bound re-admitted the row it
        // resumed from; a missing one means it excluded the row after it. Both look like a
        // perfectly ordinary page.
        var seen = await WalkAsync();

        Assert.Equal(Conversations, seen.Count);
        Assert.Equal(Conversations, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_walk_is_in_one_order_from_most_recent_backwards()
    {
        // Most recent first, and the order must not change between pages: an operator scanning
        // for a conversation they remember is relying on the list being sorted the way it says.
        var seen = await WalkAsync();

        Assert.Equal(
            [.. Enumerable.Range(0, Conversations)
                .Select(i => $"c{Conversations - 1 - i:D4}")],
            seen);
    }

    [Fact]
    public async Task A_named_participant_narrows_a_year_to_what_that_agent_was_party_to()
    {
        // The one interaction between opening the console and opening a transcript. Without it,
        // reaching a conversation in a year of history means paging, and the reachability the
        // console promises is a claim about this filter rather than about the page.
        var rows = await _conversations.ListAsync(
            participant: AgentName.Create(Agent(3)), limit: 500, cancellationToken: Ct);

        Assert.NotEmpty(rows);

        Assert.All(rows, row => Assert.Contains(
            AgentName.Create(Agent(3)), row.Participants));
    }

    [Fact]
    public async Task A_window_of_dates_returns_only_what_happened_inside_it()
    {
        // A year is only navigable if the range bound is exact at both ends. An off-by-one here
        // silently hides the conversation on the boundary, which is the one somebody looking at
        // a particular day is most likely to want.
        var from = At(100);
        var to = At(110);

        var rows = await _conversations.ListAsync(
            sinceUtc: from, untilUtc: to, limit: 500, cancellationToken: Ct);

        Assert.NotEmpty(rows);

        Assert.All(rows, row =>
        {
            Assert.True(row.LastActivityUtc >= from);
            Assert.True(row.FirstActivityUtc <= to);
        });
    }

    [Fact]
    public async Task Conversations_sharing_an_instant_are_all_walked()
    {
        // The tie. The order breaks it on the identifier, and the resume bound did not: it asked
        // for rows strictly before the last timestamp seen, so a page boundary landing inside a
        // group that shares an instant dropped every remaining member of that group.
        //
        // Reachable without contriving it. The Windows system clock ticks about every 15.6ms, so
        // two agents answering at the same moment are written with the same timestamp -- and the
        // conversation that vanishes is simply absent, with nothing anywhere saying so.
        var when = At(1000);

        for (var i = 0; i < 10; i++)
        {
            Seed($"t{i:D2}", Agent(i), Agent(i + 1), when);
        }

        // Bounded to the window they all land in, so this walks the ten tied conversations and
        // nothing
        // else. A page size smaller than the group is the whole point: it forces a boundary to
        // land inside it.
        var seen = new List<string>();
        DateTimeOffset? token = null;
        string? tokenId = null;

        for (var page = 0; page < 12; page++)
        {
            var rows = await _conversations.ListAsync(
                sinceUtc: when,
                untilUtc: when.AddMinutes(5),
                limit: 3,
                beforeLastActivityUtc: token,
                beforeId: tokenId,
                cancellationToken: Ct);

            if (rows.Count == 0)
            {
                break;
            }

            seen.AddRange(rows.Select(r => r.Id));
            token = rows[^1].LastActivityUtc;
            tokenId = rows[^1].Id;
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(10, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Counts_are_not_multiplied_by_the_messages_on_a_conversation()
    {
        // Each conversation here holds three messages. A join that multiplied would report the
        // count as nine, or the page as three rows of the same conversation -- and at this
        // volume nobody would notice which.
        var rows = await _conversations.ListAsync(limit: PageSize, cancellationToken: Ct);

        Assert.All(rows, row => Assert.Equal(3, row.Messages));
    }
}
