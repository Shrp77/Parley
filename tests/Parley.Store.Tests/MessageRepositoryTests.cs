using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

public sealed class MessageRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string Payload = Work.Request();

    private readonly SqliteConnection _connection;
    private readonly MessageRepository _messages;
    private readonly ExchangeRepository _exchanges;
    private readonly long _exchangeId;

    public MessageRepositoryTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing, Inventory);

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);

        // The exchange is admitted without its Request, so each test decides what is written on it.
        _exchangeId = _exchanges
            .TryOpenAsync(Orders, Billing, T0, MessageIdentity.ComputeRequest(Orders, Billing, Payload), Work.Conversation,
                cancellationToken: TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult().Exchange!.Id;
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<AppendOutcome> Append(
        string? payload = null,
        MessageType type = MessageType.Request,
        AgentName? from = null,
        AgentName? to = null,
        DateTimeOffset? at = null,
        long? exchangeId = null) =>
        _messages.AppendAsync(
            exchangeId ?? _exchangeId,
            type,
            from ?? Orders,
            to ?? Billing,
            Work.Model,
            payload ?? Payload,
            at ?? T0,
            cancellationToken: Token);

    [Fact]
    public async Task Appending_persists_and_round_trips()
    {
        var outcome = await Append();

        Assert.True(outcome.Stored);

        var loaded = await _messages.GetAsync(outcome.Message.Id, cancellationToken: Token);

        Assert.NotNull(loaded);
        Assert.Equal(_exchangeId, loaded.ExchangeId);
        Assert.Equal(MessageType.Request, loaded.Type);
        Assert.Equal(Orders, loaded.From);
        Assert.Equal(Billing, loaded.To);
        Assert.Equal(Work.Model, loaded.ResponderModel);
        Assert.Equal(Payload, loaded.PayloadJson);
        Assert.Equal(Work.Conversation, loaded.ConversationId);
        Assert.Equal(T0, loaded.CreatedUtc);
        Assert.Null(loaded.DeliveredUtc);
    }

    // ---- deduplication ----

    [Fact]
    public async Task An_identical_message_inside_the_window_is_refused()
    {
        var first = await Append();
        var second = await Append(at: T0.AddSeconds(30));

        Assert.True(first.Stored);
        Assert.False(second.Stored);

        // The earlier message is returned rather than a bare refusal, so a caller can report what the retry
        // collided with.
        Assert.Equal(first.Message.Id, second.Message.Id);
        Assert.Single(await _messages.GetPendingAsync(Billing, cancellationToken: Token));
    }

    [Fact]
    public async Task An_identical_message_outside_the_window_is_appended()
    {
        // The window absorbs transport retries. It does not police a participant that genuinely says the
        // same thing again later.
        var first = await Append();
        var second = await Append(at: T0.Add(MessageIdentity.DeduplicationWindow).AddSeconds(1));

        Assert.True(second.Stored);
        Assert.NotEqual(first.Message.Id, second.Message.Id);
    }

    [Fact]
    public async Task The_window_boundary_is_exclusive_of_the_cutoff()
    {
        // Exactly one window later is outside it. Pinned because the check is a strict inequality against
        // the cutoff, and an off-by-one here silently changes the rule.
        await Append();

        Assert.True((await Append(at: T0.Add(MessageIdentity.DeduplicationWindow))).Stored);
    }

    [Fact]
    public async Task A_single_changed_character_is_not_a_duplicate()
    {
        await Append();

        Assert.True((await Append(payload: Work.Request("is the invoice total gross or net!"))).Stored);
    }

    // ---- argument and constraint failures ----

    [Fact]
    public async Task A_participant_cannot_message_itself()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Append(to: Orders));
    }

    [Fact]
    public async Task Every_message_belongs_to_an_exchange()
    {
        // Every message is answered, so every one is on an
        // exchange, and the conversation is read from it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Append(exchangeId: 9999));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_unattributed_message_is_rejected_before_it_reaches_the_schema(string? model)
    {
        // The schema would refuse this too, but an argument exception names the parameter at fault.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _messages.AppendAsync(
            _exchangeId, MessageType.Request, Orders, Billing, model!, Payload, T0, cancellationToken: Token));
    }

    [Fact]
    public async Task An_over_length_payload_is_refused_by_the_schema()
    {
        // Terseness is a context-poisoning defence, and the cap lives in the schema so no code path can route
        // around it — including this one.
        var huge = Work.Request(new string('x', MessageLimits.MaxPayloadCharacters));

        await Assert.ThrowsAsync<SqliteException>(() => Append(payload: huge));
    }

    [Fact]
    public async Task A_payload_that_is_not_json_is_refused_by_the_schema()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Append(payload: "not json at all"));
    }

    // ---- inbox ----

    [Fact]
    public async Task The_inbox_holds_only_undelivered_messages_for_that_participant()
    {
        var toBilling = await Append();
        var toInventory = await Work.OpenAsync(_connection, Orders, Inventory, T0, cancellationToken: Token);

        Assert.Single(await _messages.GetPendingAsync(Billing, cancellationToken: Token));
        Assert.Equal(toInventory.Request.Id, Assert.Single(await _messages.GetPendingAsync(Inventory, cancellationToken: Token)).Id);
        Assert.Empty(await _messages.GetPendingAsync(Orders, cancellationToken: Token));

        Assert.True(await _messages.MarkDeliveredAsync(toBilling.Message.Id, T0.AddMinutes(1), Token));

        Assert.Empty(await _messages.GetPendingAsync(Billing, cancellationToken: Token));
    }

    [Fact]
    public async Task A_limited_drain_returns_the_oldest_messages_only()
    {
        // The per-message cap bounds one message; nothing bounds a backlog. This limit is what stands between
        // an undrained inbox and a context window, so it takes from the oldest end.
        var ids = new List<long>();

        for (var i = 0; i < 5; i++)
        {
            var (_, request) = await Work.OpenAsync(
                _connection, Inventory, Billing, T0.AddSeconds(i), Work.Request($"request {i}"), cancellationToken: Token);
            ids.Add(request.Id);
        }

        var page = await _messages.GetPendingAsync(Billing, limit: 2, cancellationToken: Token);

        Assert.Equal(ids.Take(2), page.Select(m => m.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_drain_limit_is_rejected(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _messages.GetPendingAsync(Billing, limit, Token));
    }

    [Fact]
    public async Task Delivering_twice_is_refused()
    {
        // Guarded on the message still being pending, so a concurrent second drain cannot overwrite the first
        // delivery's timestamp with its own.
        var outcome = await Append();

        Assert.True(await _messages.MarkDeliveredAsync(outcome.Message.Id, T0.AddMinutes(1), Token));
        Assert.False(await _messages.MarkDeliveredAsync(outcome.Message.Id, T0.AddMinutes(2), Token));

        var loaded = await _messages.GetAsync(outcome.Message.Id, cancellationToken: Token);
        Assert.Equal(T0.AddMinutes(1), loaded!.DeliveredUtc);
    }

    [Fact]
    public async Task Delivering_a_message_that_does_not_exist_reports_failure()
    {
        Assert.False(await _messages.MarkDeliveredAsync(9999, T0, Token));
    }

    // ---- conversations ----

    [Fact]
    public async Task Every_message_on_an_exchange_is_in_its_conversation()
    {
        // The exchange holds the conversation and everything on it follows (§5.0). There is no parameter
        // through which a Clarify or a Result could name a different one.
        var (exchange, request) = await Work.OpenAsync(
            _connection, Orders, Billing, T0, Work.Request("where does price live"), "pricing-boundary", cancellationToken: Token);

        var clarify = await Append(
            payload: """{"text":"gross or net of which tax?"}""", type: MessageType.Clarify,
            from: Billing, to: Orders, exchangeId: exchange.Id);

        Assert.Equal("pricing-boundary", request.ConversationId);
        Assert.Equal("pricing-boundary", clarify.Message.ConversationId);

        var conversation = await _messages.GetConversationAsync("pricing-boundary", Token);
        Assert.Equal([request.Id, clarify.Message.Id], conversation.Select(m => m.Id));
    }

    [Fact]
    public async Task A_conversation_holds_only_its_own_messages()
    {
        await Work.OpenAsync(_connection, Orders, Billing, T0, Work.Request("where does price live"), "pricing-boundary", cancellationToken: Token);
        await Work.OpenAsync(_connection, Orders, Inventory, T0, Work.Request("how much stock"), "stock-levels", cancellationToken: Token);

        Assert.Single(await _messages.GetConversationAsync("pricing-boundary", Token));
        Assert.Empty(await _messages.GetConversationAsync("never-used", Token));
    }

    [Fact]
    public async Task Exporting_without_an_identifier_is_rejected()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _messages.GetConversationAsync("", Token));
    }

    [Fact]
    public async Task A_result_is_found_beside_the_request_it_closes()
    {
        // What the envelope quotes back to the requester (§13): the Request as sent, read from the exchange
        // rather than from anything the Result says about itself.
        var (exchange, request) = await Work.OpenAsync(_connection, Orders, Inventory, T0, cancellationToken: Token);
        await Work.CloseAsync(_connection, exchange, T0.AddMinutes(1), cancellationToken: Token);

        var found = await _messages.GetRequestAsync(exchange.Id, cancellationToken: Token);

        Assert.Equal(request.Id, found!.Id);
    }

    // ---- redelivery ----

    [Fact]
    public async Task Redelivery_returns_a_message_to_the_inbox_without_writing_a_new_one()
    {
        // Redelivery, not re-sending. A fresh row would make the log a record of delivery attempts rather than
        // of what was said, and after a long outage would fall outside the deduplication window and double it.
        var outcome = await Append();
        await _messages.MarkDeliveredAsync(outcome.Message.Id, T0.AddMinutes(1), Token);

        Assert.True(await _messages.RedeliverAsync(outcome.Message.Id, Token));

        var pending = await _messages.GetPendingAsync(Billing, cancellationToken: Token);

        Assert.Equal(outcome.Message.Id, Assert.Single(pending).Id);
        Assert.Null(pending[0].DeliveredUtc);
    }

    [Fact]
    public async Task Redelivering_a_message_that_does_not_exist_reports_failure()
    {
        Assert.False(await _messages.RedeliverAsync(9999, Token));
    }

    [Fact]
    public async Task The_latest_owed_message_is_the_most_recent_one_addressed_to_the_participant()
    {
        // What redelivery hands back. An exchange holds traffic in both directions, and the participant that
        // owes a move needs the last thing said to it, not the last thing said.
        await Append();
        await Append(payload: """{"text":"gross of what tax?"}""", type: MessageType.Clarify, from: Billing, to: Orders);

        var owed = await _messages.GetLatestOwedAsync(_exchangeId, Orders, Token);

        Assert.Equal(MessageType.Clarify, owed!.Type);
        Assert.Equal(Orders, owed.To);
    }

    [Fact]
    public async Task An_exchange_with_nothing_addressed_to_the_participant_has_nothing_owed()
    {
        // Redelivery must cope rather than assume a message exists: an exchange is admitted before its Request
        // is written.
        Assert.Null(await _messages.GetLatestOwedAsync(_exchangeId, Orders, Token));
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _messages.AppendAsync(
            _exchangeId, MessageType.Request, Orders, Billing, Work.Model, Payload, T0, cancellationToken: cancelled.Token));
    }
}
