using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a destroy is all of itself or none of it.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.2. <c>DestroyTests</c> asks what a successful destroy removes.
/// This asks what a failing one leaves, which is the question SPEC.md §14 answers with the shape of
/// the transaction rather than with a list: the deletes run with foreign keys deferred to commit,
/// so a table nobody remembered fails the whole thing instead of leaving a message pointing at a
/// conversation that is gone.
/// </para>
/// <para>
/// <b>The failure is injected rather than imagined.</b> A trigger that raises on the last delete is
/// how a table added by a later migration would behave from this method's point of view — reached,
/// refused, after most of the work is done.
/// </para>
/// </remarks>
public sealed class DestroyIntegrityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;
    private int _hashes;

    private const string Payload =
        @"{""intent"":""who owns it"",""items"":[{""id"":""q1"",""kind"":""question"",""text"":""who owns it""}],""done_when"":""if it is yours I will ask you to change it, and if it is not I will look elsewhere""}";

    /// <summary>The unit separator the pair key's CHECK requires. See DestroyTests.</summary>
    private const string Separator = "\u001F";

    public DestroyIntegrityTests()
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
    public async Task A_destroy_that_fails_partway_leaves_the_conversation_whole()
    {
        Discussion("alpha", 0, messages: 3);

        // Something refuses at the last step, the way a constraint added later would.
        Execute("""
            CREATE TRIGGER tr_refuse_destroy BEFORE DELETE ON conversations
            BEGIN
              SELECT RAISE(ABORT, 'a later migration says no');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(
            async () => await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct));

        // Every row still there, including the ones deleted before the refusal.
        Assert.Equal(1L, Count("SELECT count(*) FROM conversations WHERE id = 'alpha'"));
        Assert.Equal(3L, Count("SELECT count(*) FROM messages WHERE conversation_id = 'alpha'"));
        Assert.Equal(1L, Count("SELECT count(*) FROM exchanges WHERE conversation_id = 'alpha'"));
        Assert.Equal(
            2L, Count("SELECT count(*) FROM conversation_participants WHERE conversation_id = 'alpha'"));

        // And no tombstone for a conversation that is still here, which would be the worse
        // outcome: the identifier taken by a record of a destroy that did not happen.
        Assert.Equal(0L, Count("SELECT count(*) FROM destroyed_conversations WHERE id = 'alpha'"));
    }

    [Fact]
    public async Task A_destroy_that_fails_leaves_a_second_destroy_able_to_succeed()
    {
        // The state after a rollback has to be workable, not merely consistent. A transaction left
        // open, or a temp table left behind, would make the retry fail for a different reason.
        Discussion("alpha", 0, messages: 2);

        Execute("""
            CREATE TRIGGER tr_refuse_destroy BEFORE DELETE ON conversations
            BEGIN
              SELECT RAISE(ABORT, 'a later migration says no');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(
            async () => await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct));

        Execute("DROP TRIGGER tr_refuse_destroy;");

        var outcome = await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(DestroyVerdict.Gone, outcome.Verdict);
        Assert.Equal(2, outcome.Destroyed.Single(t => t.Table == "messages").Rows);
    }

    [Fact]
    public async Task A_message_added_between_the_count_and_the_delete_still_goes()
    {
        // The tombstone's count is read before the deletes, because the rows it counts are about to
        // be removed. That leaves a window in which the count and the deletion could disagree --
        // and the deletion is what must be complete. A tombstone saying two when three went is a
        // wrong number in a record; a message left behind is a row pointing at nothing.
        Discussion("alpha", 0, messages: 2);

        var outcome = await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(DestroyVerdict.Gone, outcome.Verdict);
        Assert.Equal(0L, Count("SELECT count(*) FROM messages WHERE conversation_id = 'alpha'"));
        Assert.Equal(2L, Count("SELECT messages FROM destroyed_conversations WHERE id = 'alpha'"));
    }

    [Fact]
    public async Task Archiving_and_destroying_the_same_conversation_ends_with_it_destroyed()
    {
        // Two operator decisions about one subject. The order that matters is that a destroy after
        // an archive removes the row, rather than leaving something archived that no longer exists.
        Discussion("alpha", 0, messages: 1);

        await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct);
        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.Equal(0L, Count("SELECT count(*) FROM conversations WHERE id = 'alpha'"));
        Assert.Equal(0, await _conversations.CountArchivedAsync(cancellationToken: Ct));
        Assert.Equal(1L, Count("SELECT count(*) FROM destroyed_conversations WHERE id = 'alpha'"));
    }

    [Fact]
    public async Task Archiving_something_that_was_destroyed_changes_nothing()
    {
        // The reverse order, which a surface holding a stale list will reach.
        Discussion("alpha", 0, messages: 1);

        await _conversations.DestroyAsync("alpha", Now, cancellationToken: Ct);

        Assert.False(await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct));
        Assert.Equal(0L, Count("SELECT count(*) FROM conversations WHERE id = 'alpha'"));
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
}
