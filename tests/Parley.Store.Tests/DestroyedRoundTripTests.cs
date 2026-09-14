using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That what the tombstone stores comes back as what went in.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.7 — the first after T035, which reset every counter. The record
/// is written by one method and read by another, and between them the participants become a single
/// string and the instant becomes text. Both are lossy shapes if the round trip is wrong, and both
/// are read exactly once in the life of a conversation: after it is gone, when there is nothing
/// left to check the answer against.
/// </para>
/// <para>
/// That is what makes this worth probing rather than reading. Every other read in this repository
/// can be compared with the thing it describes; this one cannot.
/// </para>
/// </remarks>
public sealed class DestroyedRoundTripTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;
    private int _hashes;

    private const string Payload =
        @"{""intent"":""who owns it"",""items"":[{""id"":""q1"",""kind"":""question"",""text"":""who owns it""}],""done_when"":""if it is yours I will ask you to change it, and if it is not I will look elsewhere""}";

    /// <summary>The unit separator the pair key's CHECK requires. See DestroyTests.</summary>
    private const string Separator = "\u001F";

    public DestroyedRoundTripTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _conversations = new ConversationRepository(_connection);

        Execute("""
            INSERT INTO nodes (machine, connected, first_seen_utc)
            VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test','test','full','active'),
                ('billing@test','test','full','active');
            """);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => new(2026, 3, 4, 17, 45, 13, TimeSpan.Zero);

    [Fact]
    public async Task The_instant_written_is_the_instant_read()
    {
        // Stored as text and parsed back. A format mismatch here would be invisible until somebody
        // asked about a destroy, at which point there is nothing left to compare it with.
        Discussion("alpha", messages: 2);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        var record = await _conversations.GetDestroyedAsync("alpha", Ct);

        Assert.NotNull(record);
        Assert.Equal(Now, record!.DestroyedUtc);
    }

    [Fact]
    public async Task Every_participant_comes_back_and_none_are_invented()
    {
        Discussion("alpha", messages: 1);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        var record = await _conversations.GetDestroyedAsync("alpha", Ct);

        Assert.Equal(
            ["billing@test", "orders@test"],
            record!.Participants.Select(agent => agent.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_conversation_nobody_was_party_to_records_no_participants()
    {
        // Reachable: an identifier named by a message that never opened an exchange has no
        // participant rows. An empty list is the honest answer and must not become one empty name.
        Execute("""
            INSERT INTO conversations (id, state, opened_utc) VALUES ('lonely', 'OPEN', '2026-01-01T00:00:00.0000000+00:00');
            """);

        await _conversations.DestroyAsync("lonely", Now, cancellationToken: Ct);

        var record = await _conversations.GetDestroyedAsync("lonely", Ct);

        Assert.NotNull(record);
        Assert.Empty(record!.Participants);
    }

    [Fact]
    public async Task The_count_is_what_was_destroyed_rather_than_what_remained()
    {
        Discussion("alpha", messages: 7);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        var record = await _conversations.GetDestroyedAsync("alpha", Ct);

        Assert.Equal(7, record!.Messages);
    }

    [Fact]
    public async Task Asking_about_a_conversation_that_was_never_destroyed_returns_nothing()
    {
        // Null and a record are two different answers. Null plus an empty message list is how a
        // caller knows an identifier never named anything at all.
        Discussion("alpha", messages: 1);

        Assert.Null(await _conversations.GetDestroyedAsync("alpha", Ct));
        Assert.Null(await _conversations.GetDestroyedAsync("never-existed", Ct));
    }

    [Fact]
    public async Task A_second_read_gives_the_same_answer_as_the_first()
    {
        // It is the only account of those rows that will ever exist, so it had better not be
        // consumed by being read.
        Discussion("alpha", messages: 3);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        var once = await _conversations.GetDestroyedAsync("alpha", Ct);
        var again = await _conversations.GetDestroyedAsync("alpha", Ct);

        // **Field by field, not `Assert.Equal(once, again)`, and that was worth finding out.** This
        // is a record, so the obvious assertion looks right and fails: a record's generated equality
        // compares its members with `Equals`, and a list member compares by reference. Two reads of
        // the same row are never equal.
        //
        // Not a defect in this type -- every record in this repository that carries a collection
        // behaves the same way, and so does every record in the language. It is recorded because the
        // assertion that fails is the one somebody writes first.
        Assert.Equal(once!.Id, again!.Id);
        Assert.Equal(once.DestroyedUtc, again.DestroyedUtc);
        Assert.Equal(once.Messages, again.Messages);
        Assert.Equal(once.Participants, again.Participants);
    }

    [Fact]
    public async Task Destroying_two_conversations_keeps_two_records_apart()
    {
        Discussion("alpha", messages: 2);
        Discussion("beta", messages: 5, minute: 10);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);
        await _conversations.DestroyAsync("beta", Now.AddHours(1), cancellationToken: Ct);

        var alpha = await _conversations.GetDestroyedAsync("alpha", Ct);
        var beta = await _conversations.GetDestroyedAsync("beta", Ct);

        Assert.Equal(2, alpha!.Messages);
        Assert.Equal(5, beta!.Messages);
        Assert.NotEqual(alpha.DestroyedUtc, beta.DestroyedUtc);
    }

    private void Discussion(string id, int messages, int minute = 0)
    {
        Execute($"""
            INSERT INTO conversations (id, state, opened_utc, closed_utc)
            VALUES ('{id}', 'OPEN', '{Stamp(minute)}', NULL);

            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('{id}', 'orders@test', '{Stamp(minute)}'),
                   ('{id}', 'billing@test', '{Stamp(minute)}');

            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 closed_utc, clarify_depth, uncollected_flag_ms, stalled_flag_ms, request_hash, outcome)
            VALUES ('billing@test{Separator}orders@test', 'orders@test', 'billing@test',
                    'billing@test', 'OPEN', '{id}', '{Stamp(minute)}',
                    NULL, 0, 900000, 14400000, 'request-hash-{id}', NULL);
            """);

        var exchange = (long)(ExecuteScalar("SELECT last_insert_rowid();") ?? 0L);

        for (var i = 0; i < messages; i++)
        {
            _hashes++;

            Execute($"""
                INSERT INTO messages
                    (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                     content_hash, conversation_id, created_utc)
                VALUES ({exchange}, 'request', 'orders@test', 'billing@test', 'some-model-9',
                        '{Payload}', '{_hashes:x64}', '{id}', '{Stamp(minute + i)}');
                """);
        }
    }

    private static string Stamp(int minute) => $"2026-01-01T00:{minute:D2}:00.0000000+00:00";

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
}
