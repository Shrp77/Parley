using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That destroying one conversation removes all of it and none of another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves, and the second is the one worth having.</b> The obvious test is that a message
/// of the destroyed conversation does not survive, and it passes against a delete written far too
/// wide — one that took every message in the store would satisfy it perfectly. So the second half
/// asserts that a message of a <i>different</i> conversation is still there, which is the case a
/// cascade written one join too far actually breaks.
/// </para>
/// <para>
/// 005 shipped a test named
/// <c>Withdrawing_one_agent_leaves_another_agents_launch_configuration_alone</c> that passed
/// throughout the life of the defect it names, because its fixture put the two agents somewhere
/// they could not collide. The fixture here deliberately shares everything it can — the same two
/// agents, adjacent timestamps, the same pair key — so that a delete keyed on anything other than
/// the conversation identifier takes the wrong rows.
/// </para>
/// </remarks>
[Integration]
public sealed class DestroyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;
    private int _hashes;

    private const string Payload =
        @"{""ask"":""who owns the invoice total, and does it include tax?""}";

    public DestroyTests()
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

    private static DateTimeOffset Now => new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Nothing_of_the_destroyed_conversation_survives()
    {
        Discussion("alpha", 0, messages: 3);

        var outcome = await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(DestroyVerdict.Gone, outcome.Verdict);
        Assert.Equal(0L, Count("SELECT count(*) FROM conversations WHERE id = 'alpha'"));
        Assert.Equal(0L, Count("SELECT count(*) FROM messages WHERE conversation_id = 'alpha'"));
        Assert.Equal(0L, Count("SELECT count(*) FROM exchanges WHERE conversation_id = 'alpha'"));
        Assert.Equal(
            0L, Count("SELECT count(*) FROM conversation_participants WHERE conversation_id = 'alpha'"));
    }

    [Fact]
    public async Task A_different_conversation_is_left_entirely_alone()
    {
        // Same agents, same pair, adjacent minutes. Everything a too-wide delete could key on is
        // shared except the identifier.
        Discussion("alpha", 0, messages: 3);
        Discussion("beta", 1, messages: 4);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(1L, Count("SELECT count(*) FROM conversations WHERE id = 'beta'"));
        Assert.Equal(4L, Count("SELECT count(*) FROM messages WHERE conversation_id = 'beta'"));
        Assert.Equal(1L, Count("SELECT count(*) FROM exchanges WHERE conversation_id = 'beta'"));
        Assert.Equal(
            2L, Count("SELECT count(*) FROM conversation_participants WHERE conversation_id = 'beta'"));
    }

    [Fact]
    public async Task The_agents_themselves_are_not_touched()
    {
        // A conversation is not its participants. Destroying one must not take the registrations
        // with it -- that is purge, and it is a different operation with a different door.
        Discussion("alpha", 0, messages: 2);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(2L, Count("SELECT count(*) FROM agents"));
    }

    [Fact]
    public async Task The_toll_names_what_went_and_omits_what_did_not()
    {
        Discussion("alpha", 0, messages: 3);

        var outcome = await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(3, outcome.Destroyed.Single(t => t.Table == "messages").Rows);
        Assert.Equal(1, outcome.Destroyed.Single(t => t.Table == "exchanges").Rows);

        // The conversation's own row is not tolled: the toll is an account of collateral, and the
        // conversation is the thing that was asked for.
        Assert.DoesNotContain(outcome.Destroyed, t => t.Table == "conversations");

        // A table that lost nothing is absent rather than present with zero.
        Assert.DoesNotContain(outcome.Destroyed, t => t.Rows == 0);
    }

    [Fact]
    public async Task A_conversation_that_is_not_there_is_reported_rather_than_thrown()
    {
        var outcome = await _conversations.DestroyAsync("nothing", Now, cancellationToken: Ct);

        Assert.Equal(DestroyVerdict.NoSuchConversation, outcome.Verdict);
        Assert.Empty(outcome.Destroyed);
    }

    [Fact]
    public async Task The_tombstone_records_the_facts_and_none_of_the_content()
    {
        Discussion("alpha", 0, messages: 3);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(1L, Count("SELECT count(*) FROM destroyed_conversations WHERE id = 'alpha'"));
        Assert.Equal(3L, Count("SELECT messages FROM destroyed_conversations WHERE id = 'alpha'"));

        var participants = Scalar("SELECT participants FROM destroyed_conversations WHERE id = 'alpha'");

        Assert.Contains("orders@test", participants, StringComparison.Ordinal);
        Assert.Contains("billing@test", participants, StringComparison.Ordinal);

        // The whole row, read as text, must carry nothing of what was said. `who owns it` is in
        // every message payload the fixture wrote.
        var row = Scalar(
            """
            SELECT id || ' ' || destroyed_utc || ' ' || messages || ' ' || participants
              FROM destroyed_conversations WHERE id = 'alpha';
            """);

        Assert.DoesNotContain("who owns it", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_identifier_stays_taken_so_nothing_can_reuse_it()
    {
        // Without the tombstone the primary key is free the instant the row goes, and a new
        // conversation could open under the name of one somebody holds an export of.
        Discussion("alpha", 0, messages: 1);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Throws<SqliteException>(() => Execute("""
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES ('alpha', '2026-01-03T00:00:00.0000000+00:00', 0, '');
            """));
    }

    private void Discussion(string id, int minute, int messages)
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

    /// <summary>The unit separator the pair key's CHECK requires between the two names.</summary>
    /// <remarks>
    /// Written as an escape in an ordinary string literal rather than pasted in as itself.
    /// A raw string literal does not process escapes, so the separator cannot be written
    /// inline where it is needed -- and pasting the character itself puts an invisible byte
    /// in a fixture, which is not something a reviewer can see. SPEC.md 9.3 keeps a CHECK
    /// that the stored key equals min || char(31) || max, because cycle detection and
    /// retention both key on it; a fixture inventing its own separator is refused, which is
    /// the constraint doing its job.
    /// </remarks>
    private const string Separator = "\u001F";

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

    private long Count(string sql) => Convert.ToInt64(ExecuteScalar(sql), provider: null);

    private string Scalar(string sql) => ExecuteScalar(sql)?.ToString() ?? string.Empty;
}
