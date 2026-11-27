using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That archiving is durable, orthogonal to closure, and does not stop anything being delivered.
/// </summary>
/// <remarks>
/// <para>
/// The property most worth checking is the one the design argument turns on: archiving is
/// <b>not</b> a fourth value of <c>state</c>. A conversation may be open and archived, or closed
/// and archived, and neither fact may displace the other. An implementation that folded them
/// together would pass any test that only ever archived closed conversations.
/// </para>
/// <para>
/// Durability is checked by reading through a second repository on the same connection rather than
/// by trusting the return value, because a method that reported success and wrote nothing is
/// exactly the failure being guarded against.
/// </para>
/// </remarks>
[Integration]
public sealed class ArchiveTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;
    private int _hashes;

    private const string Payload =
        @"{""ask"":""who owns the invoice total, and does it include tax?""}";

    /// <summary>The unit separator the pair key's CHECK requires. See DestroyTests.</summary>
    private const string Separator = "\u001F";

    public ArchiveTests()
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
    public async Task Archiving_is_read_back_by_a_second_reader()
    {
        Discussion("alpha", 0, messages: 2);

        Assert.True(await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct));

        var again = new ConversationRepository(_connection);
        var listed = await again.ListAsync(includeArchived: true, cancellationToken: Ct);

        Assert.True(listed.Single(c => c.Id == "alpha").Archived);
    }

    [Fact]
    public async Task An_archived_conversation_is_absent_unless_asked_for()
    {
        Discussion("alpha", 0, messages: 2);
        Discussion("beta", 1, messages: 2);

        await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct);

        var ordinary = await _conversations.ListAsync(cancellationToken: Ct);
        var everything = await _conversations.ListAsync(includeArchived: true, cancellationToken: Ct);

        Assert.DoesNotContain(ordinary, c => c.Id == "alpha");
        Assert.Contains(ordinary, c => c.Id == "beta");
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public async Task The_omitted_count_says_how_many_are_being_hidden()
    {
        // Without this a surface hiding archived conversations would be hiding traffic, since
        // archiving does not stop a conversation receiving messages, and would have no way to know.
        Discussion("alpha", 0, messages: 2);
        Discussion("beta", 1, messages: 2);
        Discussion("gamma", 2, messages: 2);

        await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct);
        await _conversations.SetArchivedAsync("gamma", true, Now, cancellationToken: Ct);

        Assert.Equal(2, await _conversations.CountArchivedAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Unarchiving_returns_it_to_the_ordinary_listing()
    {
        Discussion("alpha", 0, messages: 2);

        await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct);

        Assert.True(await _conversations.SetArchivedAsync("alpha", false, Now, cancellationToken: Ct));

        var ordinary = await _conversations.ListAsync(cancellationToken: Ct);

        Assert.Contains(ordinary, c => c.Id == "alpha");
        Assert.False(ordinary.Single(c => c.Id == "alpha").Archived);
        Assert.Equal(0, await _conversations.CountArchivedAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Archiving_twice_reports_no_change_and_does_not_move_the_timestamp()
    {
        // An operator who archived something last week and clicks again has not re-archived it
        // today, and a surface showing the second date would report an event that did not happen.
        Discussion("alpha", 0, messages: 1);

        await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct);

        var first = Scalar("SELECT archived_utc FROM conversations WHERE id = 'alpha'");

        var again = await _conversations.SetArchivedAsync(
            "alpha", true, Now.AddDays(7), cancellationToken: Ct);

        Assert.False(again);
        Assert.Equal(first, Scalar("SELECT archived_utc FROM conversations WHERE id = 'alpha'"));
    }

    [Fact]
    public async Task Unarchiving_something_that_is_not_archived_reports_no_change()
    {
        Discussion("alpha", 0, messages: 1);

        Assert.False(await _conversations.SetArchivedAsync("alpha", false, Now, cancellationToken: Ct));
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("CLOSED")]
    public async Task Archiving_leaves_the_closure_state_exactly_as_it_was(string state)
    {
        // The whole reason archived is a column rather than a fourth state value. A conversation
        // may be open and archived, or closed and archived, and neither fact may displace the
        // other -- an implementation that folded them together would pass a test that only ever
        // archived closed ones.
        Discussion("alpha", 0, messages: 1);

        if (state == "CLOSED")
        {
            Execute(
                "UPDATE conversations SET state = 'CLOSED', "
                + "closed_utc = '2026-01-01T01:00:00.0000000+00:00' WHERE id = 'alpha';");
        }

        await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct);

        Assert.Equal(state, Scalar("SELECT state FROM conversations WHERE id = 'alpha'"));

        var record = await _conversations.GetAsync("alpha", cancellationToken: Ct);

        Assert.NotNull(record);
    }

    [Fact]
    public async Task An_archived_conversation_still_takes_messages_and_stays_archived()
    {
        // SPEC.md §10.2: archiving is filing, not muting. And a message must not un-archive it --
        // that would let any agent reverse an operator's decision, which is the one direction of
        // authority this protocol does not have.
        Discussion("alpha", 0, messages: 1);

        await _conversations.SetArchivedAsync("alpha", true, Now, cancellationToken: Ct);

        Message("alpha", minute: 5);

        var listed = await _conversations.ListAsync(includeArchived: true, cancellationToken: Ct);
        var alpha = listed.Single(c => c.Id == "alpha");

        Assert.True(alpha.Archived);
        Assert.Equal(2, alpha.Messages);
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

        for (var i = 0; i < messages; i++)
        {
            Message(id, minute + i);
        }
    }

    private void Message(string id, int minute)
    {
        _hashes++;

        Execute($"""
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc)
            VALUES ((SELECT id FROM exchanges WHERE conversation_id = '{id}' LIMIT 1),
                    'request', 'orders@test', 'billing@test', 'some-model-9',
                    '{Payload}', '{_hashes:x64}', '{id}', '{Stamp(minute)}');
            """);
    }

    private static string Stamp(int minute) => $"2026-01-01T00:{minute:D2}:00.0000000+00:00";

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }
}
