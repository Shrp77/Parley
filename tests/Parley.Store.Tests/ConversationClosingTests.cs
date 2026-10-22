using System.Text.Json;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// How a conversation ends (§5.0): by the consensus of its participants, or by the person.
/// </summary>
/// <remarks>
/// <para>
/// Nothing else ends one, which makes both paths load-bearing. A close that left an exchange open would
/// leave a receiver working on something ended; one that closed an exchange without telling its requester
/// would report work as settled that never was. So the close and its notices are one write, and these check
/// that each party is told, in the words the schema requires.
/// </para>
/// <para>
/// <b>Silence never assents.</b> The version this replaced let a participant that did not vote agree after a
/// window, which closed conversations over agents that were simply busy doing the work.
/// </para>
/// </remarks>
public sealed class ConversationClosingTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;
    private readonly ExchangeRepository _exchanges;

    public ConversationClosingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing, Inventory);
        _conversations = new ConversationRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the person's close ----

    [Fact]
    public async Task Closing_ends_every_open_exchange_and_tells_both_parties()
    {
        var (exchange, _) = await Work.OpenAsync(
            _connection, Orders, Billing, T0, Work.RequestOf("totals", "q1", "q2"), cancellationToken: Ct);

        Assert.True(await _conversations.ForceConcludeAsync(Work.Conversation, "superseded by the pricing rewrite", T0.AddHours(1), cancellationToken: Ct));

        var closed = await _exchanges.GetAsync(exchange.Id, cancellationToken: Ct);
        Assert.Equal(ExchangeState.Closed, closed!.State);
        Assert.Equal(ExchangeOutcome.Closed, closed.Outcome);

        var notices = Notices(exchange.Id);

        Assert.Equal(["billing@test>orders@test", "orders@test>billing@test"], notices.Select(n => $"{n.From}>{n.To}").Order(StringComparer.Ordinal));
        Assert.All(notices, n => Assert.Equal(Responder.Broker, n.Responder));

        using var payload = JsonDocument.Parse(notices[0].Payload);
        var outcomes = payload.RootElement.GetProperty("outcomes").EnumerateArray().ToList();

        // One closed outcome for the ask, under the identifier a Request answered as a whole has always used
        // (§5.3).
        Assert.Equal(["request"], outcomes.Select(o => o.GetProperty("id").GetString()));
        Assert.All(outcomes, o => Assert.Equal("closed", o.GetProperty("outcome").GetString()));
        Assert.Contains("superseded by the pricing rewrite", outcomes[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("person", payload.RootElement.GetProperty("closed_by").GetString());

        Assert.Equal("CLOSED|person", Row($"SELECT state, closed_by FROM conversations WHERE id = '{Work.Conversation}';"));
    }

    [Fact]
    public async Task Finished_work_in_the_conversation_is_left_as_it_was()
    {
        var (done, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await Work.CloseAsync(_connection, done, T0.AddMinutes(1), cancellationToken: Ct);

        await _conversations.ForceConcludeAsync(Work.Conversation, null, T0.AddHours(1), cancellationToken: Ct);

        Assert.Equal(ExchangeOutcome.Complete, (await _exchanges.GetAsync(done.Id, cancellationToken: Ct))!.Outcome);
        Assert.Empty(Notices(done.Id));
    }

    [Fact]
    public async Task Closing_twice_changes_nothing_the_second_time()
    {
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);

        Assert.True(await _conversations.ForceConcludeAsync(Work.Conversation, null, T0.AddHours(1), cancellationToken: Ct));
        Assert.False(await _conversations.ForceConcludeAsync(Work.Conversation, null, T0.AddHours(2), cancellationToken: Ct));

        Assert.Equal(2, Notices(exchange.Id).Count);
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_is_not_closed()
    {
        Assert.False(await _conversations.ForceConcludeAsync("never-named", null, T0, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_reason_too_long_for_a_message_does_not_stop_the_close()
    {
        // The notice matters more than its wording: a close that could not be stored is a close nobody is told
        // about, so the reason gives way rather than the message cap refusing the whole write.
        var items = Enumerable.Range(1, 60).Select(i => $"item-{i}").ToArray();
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("many", items), cancellationToken: Ct);

        Assert.True(await _conversations.ForceConcludeAsync(Work.Conversation, new string('x', 20_000), T0.AddHours(1), cancellationToken: Ct));

        Assert.All(Notices(exchange.Id), n => Assert.True(n.Payload.Length <= MessageLimits.MaxPayloadCharacters));
        Assert.Equal(ExchangeOutcome.Closed, (await _exchanges.GetAsync(exchange.Id, cancellationToken: Ct))!.Outcome);
    }

    // ---- consensus ----

    [Fact]
    public async Task Silence_does_not_assent()
    {
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        Poll(("orders@test", "done"), ("billing@test", null));

        Assert.False(await _conversations.TryConcludeAsync(Work.Conversation, T0.AddDays(30), cancellationToken: Ct));
        Assert.Equal("CLOSING", Row($"SELECT state FROM conversations WHERE id = '{Work.Conversation}';"));
    }

    [Fact]
    public async Task One_participant_not_done_keeps_it_open()
    {
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        Poll(("orders@test", "done"), ("billing@test", "not_done"));

        Assert.False(await _conversations.TryConcludeAsync(Work.Conversation, T0.AddHours(1), cancellationToken: Ct));
    }

    [Fact]
    public async Task Everyone_done_closes_it_by_consensus_and_says_so_to_whoever_still_holds_work()
    {
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        Poll(("orders@test", "done"), ("billing@test", "done"));

        Assert.True(await _conversations.TryConcludeAsync(Work.Conversation, T0.AddHours(1), cancellationToken: Ct));

        Assert.Equal("CLOSED|consensus", Row($"SELECT state, closed_by FROM conversations WHERE id = '{Work.Conversation}';"));
        Assert.Equal(ExchangeOutcome.Closed, (await _exchanges.GetAsync(exchange.Id, cancellationToken: Ct))!.Outcome);
        Assert.Equal(2, Notices(exchange.Id).Count);
    }

    [Fact]
    public async Task A_retired_participant_counts_for_nothing_either_way()
    {
        // Nobody is going to answer for it, and letting it block would leave the conversation open for ever.
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        Poll(("orders@test", "done"), ("billing@test", null));
        Execute("UPDATE agents SET status = 'retired' WHERE name = 'billing@test';");

        Assert.True(await _conversations.TryConcludeAsync(Work.Conversation, T0.AddHours(1), cancellationToken: Ct));
    }

    [Fact]
    public async Task Without_a_poll_there_is_no_consensus_to_find()
    {
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);

        Assert.False(await _conversations.TryConcludeAsync(Work.Conversation, T0.AddHours(1), cancellationToken: Ct));
    }

    private void Poll(params (string Agent, string? Vote)[] votes)
    {
        Execute($"UPDATE conversations SET state = 'CLOSING' WHERE id = '{Work.Conversation}';");

        foreach (var (agent, vote) in votes)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE conversation_participants
                   SET polled_utc = $at, vote = $vote, voted_utc = CASE WHEN $vote IS NULL THEN NULL ELSE $at END
                 WHERE conversation_id = $conversation AND agent = $agent;
                """;
            command.Parameters.AddWithValue("$at", "2026-01-01T00:30:00.0000000Z");
            command.Parameters.AddWithValue("$vote", (object?)vote ?? DBNull.Value);
            command.Parameters.AddWithValue("$conversation", Work.Conversation);
            command.Parameters.AddWithValue("$agent", agent);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
    }

    private List<(string From, string To, string Responder, string Payload)> Notices(long exchangeId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT from_agent, to_agent, responder_model, payload_json FROM messages "
          + "WHERE exchange_id = $id AND type = 'result' AND responder_model = 'parley:broker' ORDER BY id;";
        command.Parameters.AddWithValue("$id", exchangeId);

        using var reader = command.ExecuteReader();
        var notices = new List<(string, string, string, string)>();

        while (reader.Read())
        {
            notices.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return notices;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string Row(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i)));
    }
}
