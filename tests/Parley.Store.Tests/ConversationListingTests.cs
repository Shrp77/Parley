using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The listing that makes SPEC.md §10.2's export reachable, and the snapshot that keeps it honest.
/// </summary>
/// <remarks>
/// <para>
/// Without a listing, <c>export</c> takes an identifier nothing produces: it is opaque and chosen
/// by an agent (§5.0), so a person cannot know one. These check the two ways the query shape goes
/// wrong. One is the join between conversations and their messages multiplying a conversation by
/// the messages on it, which inflates every count while the list still looks sensible. The other
/// is paging by offset over a log that only grows, which shows one conversation twice and another
/// never.
/// </para>
/// <para>
/// The snapshot is checked separately because inferring state from messages is the mistake it
/// exists to prevent: an orphaned exchange and an open one carry the same messages.
/// </para>
/// </remarks>
[Integration]
public sealed class ConversationListingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;
    private readonly ExchangeRepository _exchanges;
    private int _hashes;

    /// <summary>A payload the schema accepts, kept out of the interpolation.</summary>
    /// <remarks>
    /// It is a whole Request because rule 8.1 is enforced in storage rather than in code: §9.3 puts it
    /// there so no path can write a Request that does not say what done looks like. A fixture without
    /// one is refused, which is the constraint working.
    /// </remarks>
    private const string Payload =
        @"{""ask"":""who owns the invoice total, and does it include tax?""}";

    public ConversationListingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _conversations = new ConversationRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);

        Execute("""
            INSERT INTO nodes (machine, connected, first_seen_utc)
            VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test','test','full','active'),
                ('billing@test','test','full','active'),
                ('inventory@test','test','full','active');
            """);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Stamp(int minute) =>
        $"2026-01-01T00:{minute:D2}:00.0000000+00:00";

    /// <summary>Opens a conversation with one exchange and some messages on it.</summary>
    private long Discussion(
        string id, string asker, string answerer, int minute, int messages, string state = "CLOSED",
        string outcome = "complete")
    {
        Execute($"""
            INSERT INTO conversations (id, state, opened_utc, closed_utc)
            VALUES ('{id}', 'OPEN', '{Stamp(minute)}', NULL) ON CONFLICT DO NOTHING;

            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('{id}', '{asker}', '{Stamp(minute)}'),
                   ('{id}', '{answerer}', '{Stamp(minute)}')
            ON CONFLICT DO NOTHING;

            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 closed_utc, clarify_depth, uncollected_flag_ms, stalled_flag_ms, request_hash, outcome)
            VALUES ('{Pair(asker, answerer)}', '{asker}', '{answerer}', '{answerer}', '{state}',
                    '{id}', '{Stamp(minute)}',
                    {(state == "CLOSED" ? $"'{Stamp(minute + 1)}'" : "NULL")}, 0, 900000, 14400000,
                    'request-hash-{id}',
                    {(state == "CLOSED" ? $"'{outcome}'" : "NULL")});
            """);

        var exchange = (long)(ExecuteScalar("SELECT last_insert_rowid();") ?? 0L);

        for (var i = 0; i < messages; i++)
        {
            _hashes++;

            Execute($"""
                INSERT INTO messages
                    (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                     content_hash, conversation_id, created_utc)
                VALUES ({exchange}, 'request', '{asker}', '{answerer}', 'some-model-9',
                        '{Payload}', '{_hashes:x64}', '{id}',
                        '{Stamp(minute + i)}');
                """);
        }

        return exchange;
    }

    /// <summary>The pair key exactly as the schema derives it.</summary>
    /// <remarks>
    /// Unit separator, not a pipe. §9.3 keeps a CHECK that the stored key equals
    /// <c>min || char(31) || max</c>, because cycle detection and retention both key on it and a
    /// mismatched one would make two unrelated pairs indistinguishable to every rule built on it.
    /// A fixture inventing its own separator is refused, which is the constraint doing its job.
    /// </remarks>
    private static string Pair(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? $"{a}{b}" : $"{b}{a}";

    private object? ExecuteScalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task A_conversation_is_counted_once_however_many_messages_it_holds()
    {
        // The join that multiplies. Three messages on one conversation must be one row saying
        // three, not three rows -- and a list that got this wrong would still look plausible.
        Discussion("alpha", "orders@test", "billing@test", minute: 1, messages: 3);

        var page = await _conversations.ListAsync(cancellationToken: Ct);

        var only = Assert.Single(page);
        Assert.Equal("alpha", only.Id);
        Assert.Equal(3, only.Messages);
    }

    [Fact]
    public async Task The_most_recently_active_comes_first()
    {
        Discussion("older", "orders@test", "billing@test", minute: 1, messages: 1);
        Discussion("newer", "orders@test", "inventory@test", minute: 40, messages: 1);

        var page = await _conversations.ListAsync(cancellationToken: Ct);

        Assert.Equal(["newer", "older"], page.Select(c => c.Id));
    }

    [Fact]
    public async Task Paging_by_activity_shows_each_conversation_exactly_once()
    {
        // Offset paging over a log that only grows repeats one row and drops another. Resuming
        // from the last activity seen cannot, because the key it resumes from does not move.
        Discussion("a", "orders@test", "billing@test", minute: 1, messages: 1);
        Discussion("b", "orders@test", "billing@test", minute: 2, messages: 1);
        Discussion("c", "orders@test", "billing@test", minute: 3, messages: 1);

        var first = await _conversations.ListAsync(limit: 2, cancellationToken: Ct);
        var second = await _conversations.ListAsync(
            limit: 2, beforeLastActivityUtc: first[^1].LastActivityUtc, cancellationToken: Ct);

        Assert.Equal(["c", "b"], first.Select(c => c.Id));
        Assert.Equal(["a"], second.Select(c => c.Id));
    }

    [Fact]
    public async Task Filtering_by_participant_uses_the_exchanges_rather_than_a_declaration()
    {
        Discussion("shared", "orders@test", "billing@test", minute: 1, messages: 1);
        Discussion("elsewhere", "orders@test", "inventory@test", minute: 2, messages: 1);

        var page = await _conversations.ListAsync(
            participant: AgentName.Create("billing@test"), cancellationToken: Ct);

        var only = Assert.Single(page);
        Assert.Equal("shared", only.Id);
        Assert.Contains(AgentName.Create("billing@test"), only.Participants);
    }

    [Fact]
    public async Task An_open_exchange_is_reported_as_open()
    {
        Discussion("waiting", "orders@test", "billing@test", minute: 1, messages: 1, state: "OPEN");

        var page = await _conversations.ListAsync(cancellationToken: Ct);

        Assert.True(Assert.Single(page).HasOpenExchange);
    }

    [Fact]
    public async Task A_time_window_bounds_by_last_activity_rather_than_by_opening()
    {
        // An operator asking "what happened this afternoon" is asking when a discussion was last
        // used, not when somebody first named it. A conversation opened this morning and answered
        // this afternoon belongs in the afternoon.
        Discussion("long-running", "orders@test", "billing@test", minute: 1, messages: 1);

        Execute("""
            UPDATE messages SET created_utc = '2026-01-01T00:59:00.0000000+00:00'
             WHERE conversation_id = 'long-running';
            """);

        var page = await _conversations.ListAsync(
            sinceUtc: DateTimeOffset.Parse("2026-01-01T00:30:00+00:00", null),
            cancellationToken: Ct);

        Assert.Single(page);
    }

    [Fact]
    public async Task An_exchange_the_person_closed_is_not_reported_as_a_finished_one()
    {
        // The reason the snapshot is read rather than inferred: an exchange a person closed and one its
        // receiver finished can carry the same messages, and an operator opens a conversation because
        // something went wrong.
        Discussion("abandoned", "orders@test", "billing@test", minute: 1, messages: 1, outcome: "closed");

        var snapshot = Assert.Single(await _exchanges.GetSnapshotsAsync("abandoned", Ct));

        Assert.Equal(ExchangeState.Closed, snapshot.State);
        Assert.Equal(ExchangeOutcome.Closed, snapshot.Outcome);
    }

    [Fact]
    public async Task A_conversation_nobody_has_written_to_does_not_appear()
    {
        // Named and then never used. It has no activity to sort by, and a listing that invented
        // one would put it wherever the invention landed.
        Execute("""
            INSERT INTO conversations (id, state, opened_utc) VALUES ('empty', 'OPEN', '2026-01-01T00:00:00.0000000+00:00');
            """);

        Assert.Empty(await _conversations.ListAsync(cancellationToken: Ct));
    }
}
