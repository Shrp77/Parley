using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

public sealed class MessageRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    // A question must state what the asker will do with each possible answer (rule 1),
    // and the schema refuses one that does not. Forty characters is the floor, chosen so
    // that "n/a" cannot satisfy it.
    private const string Payload = """{"text":"is the invoice total gross or net?","consequences":"If gross I stop adding tax at invoice time; if net I keep it."}""";
    private const string Model = "claude-opus-5";

    private readonly SqliteConnection _connection;
    private readonly MessageRepository _messages;
    private readonly ExchangeRepository _exchanges;
    private readonly long _exchangeId;

    public MessageRepositoryTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null, AgentStatus.Registered),
                TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();
        }

        var exchanges = new ExchangeRepository(_connection);
        _exchangeId = exchanges
            .TryOpenAsync(Orders, Billing, T0, TimeSpan.FromMinutes(15), TimeSpan.FromHours(4),
                TimeSpan.FromHours(3), "q1", cancellationToken: TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult().Exchange!.Id;

        _messages = new MessageRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);
    }

    // The conversation is chosen when the exchange is opened, not by the first message on it,
    // so a test about conversations needs its own exchange rather than the shared fixture one.
    private async Task<long> OpenInConversation(string conversationId, string question) =>
        (await _exchanges.TryOpenAsync(
            Orders, Billing, T0, TimeSpan.FromMinutes(15), TimeSpan.FromHours(4),
            TimeSpan.FromHours(3), question, conversationId,
            cancellationToken: TestContext.Current.CancellationToken)).Exchange!.Id;

    public void Dispose() => _connection.Dispose();

    private Task<AppendOutcome> Append(
        string payload = Payload,
        MessageType type = MessageType.Question,
        AgentName? from = null,
        AgentName? to = null,
        DateTimeOffset? at = null,
        long? exchangeId = -1,
        string? conversationId = null) =>
        _messages.AppendAsync(
            exchangeId == -1 ? _exchangeId : exchangeId,
            type,
            from ?? Orders,
            to ?? Billing,
            Model,
            payload,
            at ?? T0,
            conversationId,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Appending_persists_and_round_trips()
    {
        var outcome = await Append();

        Assert.True(outcome.Stored);

        var loaded = await _messages.GetAsync(outcome.Message.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(_exchangeId, loaded.ExchangeId);
        Assert.Equal(MessageType.Question, loaded.Type);
        Assert.Equal(Orders, loaded.From);
        Assert.Equal(Billing, loaded.To);
        Assert.Equal(Model, loaded.ResponderModel);
        Assert.Equal(Payload, loaded.PayloadJson);
        Assert.Equal(T0, loaded.CreatedUtc);
        Assert.Null(loaded.DeliveredUtc);
        Assert.True(loaded.IsPending);
    }

    // ---- deduplication ----

    [Fact]
    public async Task An_identical_message_inside_the_window_is_refused()
    {
        var first = await Append();
        var second = await Append(at: T0.AddSeconds(30));

        Assert.True(first.Stored);
        Assert.False(second.Stored);

        // The earlier message is returned rather than a bare refusal, so a caller can report
        // what the retry collided with.
        Assert.Equal(first.Message.Id, second.Message.Id);

        var pending = await _messages.GetPendingAsync(Billing, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(pending);
    }

    [Fact]
    public async Task An_identical_message_outside_the_window_is_appended()
    {
        // The window absorbs transport retries. It does not police an agent that genuinely
        // says the same thing again later.
        var first = await Append();
        var second = await Append(at: T0.Add(MessageIdentity.DeduplicationWindow).AddSeconds(1));

        Assert.True(first.Stored);
        Assert.True(second.Stored);
        Assert.NotEqual(first.Message.Id, second.Message.Id);
    }

    [Fact]
    public async Task The_window_boundary_is_exclusive_of_the_cutoff()
    {
        // Exactly one window later is outside it. Pinned because the check is a strict
        // inequality against the cutoff, and an off-by-one here silently changes the rule.
        await Append();
        var later = await Append(at: T0.Add(MessageIdentity.DeduplicationWindow));

        Assert.True(later.Stored);
    }

    [Fact]
    public async Task The_same_text_to_a_different_peer_is_not_a_duplicate()
    {
        // Both are notifications on no exchange, so the recipient is the only thing that
        // differs. Varying the type or the exchange as well would prove nothing about
        // routing: any one of the three would account for the result.
        var toBilling = await Append(
            type: MessageType.Notify, exchangeId: null, to: Billing, payload: """{"n":"same"}""");
        var toInventory = await Append(
            type: MessageType.Notify, exchangeId: null, to: Inventory, payload: """{"n":"same"}""");

        Assert.True(toBilling.Stored);
        Assert.True(toInventory.Stored);
        Assert.NotEqual(toBilling.Message.Id, toInventory.Message.Id);
    }

    [Fact]
    public async Task A_single_changed_character_is_not_a_duplicate()
    {
        // Byte-identical is the rule, so near-identical must pass. The two payloads differ
        // only in their final punctuation.
        await Append();
        var altered = await Append(payload: """{"text":"is the invoice total gross or net!","consequences":"If gross I stop adding tax at invoice time; if net I keep it."}""");

        Assert.True(altered.Stored);
    }

    [Fact]
    public async Task A_notification_needs_no_exchange()
    {
        var outcome = await Append(type: MessageType.Notify, exchangeId: null);

        Assert.True(outcome.Stored);
        Assert.Null(outcome.Message.ExchangeId);
    }

    // ---- argument and constraint failures ----

    [Fact]
    public async Task An_agent_cannot_message_itself()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Append(to: Orders));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_unattributed_message_is_rejected_before_it_reaches_the_schema(string? model)
    {
        // The schema would refuse this too, but an argument exception names the parameter at
        // fault, which a SqliteException does not.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _messages.AppendAsync(
            _exchangeId, MessageType.Question, Orders, Billing, model!, Payload, T0,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_over_length_payload_is_refused_by_the_schema()
    {
        // Terseness is a context-poisoning defence, and the cap lives in the schema so no code
        // path can route around it — including this one.
        var huge = $$"""{"text":"{{new string('x', 2000)}}"}""";

        await Assert.ThrowsAsync<SqliteException>(() => Append(payload: huge));
    }

    [Fact]
    public async Task A_payload_that_is_not_json_is_refused_by_the_schema()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Append(payload: "not json at all"));
    }

    [Fact]
    public async Task A_notification_carrying_an_exchange_is_refused_by_the_schema()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Append(type: MessageType.Notify));
    }

    // ---- inbox ----

    [Fact]
    public async Task The_inbox_holds_only_undelivered_messages_for_that_agent()
    {
        var toBilling = await Append();
        await Append(to: Inventory, exchangeId: null, type: MessageType.Notify);

        Assert.Single(await _messages.GetPendingAsync(Billing, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await _messages.GetPendingAsync(Inventory, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await _messages.GetPendingAsync(Orders, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(await _messages.MarkDeliveredAsync(
            toBilling.Message.Id, T0.AddMinutes(1), TestContext.Current.CancellationToken));

        Assert.Empty(await _messages.GetPendingAsync(Billing, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_inbox_is_ordered_by_identity_not_timestamp()
    {
        // Two messages can share a stored timestamp. An inbox that reordered them could
        // present an answer before the clarification it answers.
        var first = await Append(type: MessageType.Notify, exchangeId: null, payload: """{"n":1}""");
        var second = await Append(type: MessageType.Notify, exchangeId: null, payload: """{"n":2}""");
        var third = await Append(type: MessageType.Notify, exchangeId: null, payload: """{"n":3}""");

        var pending = await _messages.GetPendingAsync(Billing, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [first.Message.Id, second.Message.Id, third.Message.Id],
            pending.Select(m => m.Id));
    }

    [Fact]
    public async Task A_limited_drain_returns_the_oldest_messages_only()
    {
        // The per-message cap bounds one message; nothing bounds a backlog. Until per-sender
        // rate limits exist, this limit is the only thing between an undrained inbox and a
        // context window, so it has to take from the oldest end rather than an arbitrary one.
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var outcome = await Append(
                type: MessageType.Notify, exchangeId: null, payload: $$"""{"n":{{i}}}""");
            ids.Add(outcome.Message.Id);
        }

        var page = await _messages.GetPendingAsync(
            Billing, limit: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ids.Take(2), page.Select(m => m.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_drain_limit_is_rejected(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _messages.GetPendingAsync(
            Billing, limit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delivering_twice_is_refused()
    {
        // Guarded on the message still being pending, so a concurrent second drain cannot
        // overwrite the first delivery's timestamp with its own.
        var outcome = await Append();

        Assert.True(await _messages.MarkDeliveredAsync(
            outcome.Message.Id, T0.AddMinutes(1), TestContext.Current.CancellationToken));
        Assert.False(await _messages.MarkDeliveredAsync(
            outcome.Message.Id, T0.AddMinutes(2), TestContext.Current.CancellationToken));

        var loaded = await _messages.GetAsync(outcome.Message.Id, TestContext.Current.CancellationToken);
        Assert.Equal(T0.AddMinutes(1), loaded!.DeliveredUtc);
    }

    [Fact]
    public async Task Delivering_a_message_that_does_not_exist_reports_failure()
    {
        Assert.False(await _messages.MarkDeliveredAsync(
            9999, T0, TestContext.Current.CancellationToken));
    }

    // ---- conversations (SPEC 5.0, 10.2) ----

    [Fact]
    public async Task A_conversation_is_gathered_by_its_identifier()
    {
        // The export in SPEC 10.2. It reads across agents, which is why it is an operator
        // operation and must never reach an agent's tool set.
        var first = await Append(payload: """{"n":1}""", type: MessageType.Notify,
            exchangeId: null, conversationId: "pricing-boundary");
        await Append(payload: """{"n":2}""", type: MessageType.Notify, exchangeId: null);
        var second = await Append(payload: """{"n":3}""", type: MessageType.Notify,
            exchangeId: null, conversationId: "pricing-boundary");
        await Append(payload: """{"n":4}""", type: MessageType.Notify, exchangeId: null,
            conversationId: "invoice-rounding");

        var conversation = await _messages.GetConversationAsync(
            "pricing-boundary", TestContext.Current.CancellationToken);

        Assert.Equal([first.Message.Id, second.Message.Id], conversation.Select(m => m.Id));
    }

    [Fact]
    public async Task An_exchange_may_stand_alone()
    {
        // The common case. Most exchanges belong to no wider discussion, and an agent that
        // says nothing about grouping is not asked to invent one.
        var outcome = await Append();

        Assert.Null(outcome.Message.ConversationId);
        Assert.Empty(await _messages.GetConversationAsync(
            "never-used", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_identifier_is_not_part_of_deduplication_identity()
    {
        // It groups messages; it is not one of them. Hashing it would let an agent defeat
        // deduplication by varying which discussion it claimed the message belonged to.
        var first = await Append();
        var second = await Append(at: T0.AddSeconds(10), conversationId: "pricing-boundary");

        Assert.True(first.Stored);
        Assert.False(second.Stored);
    }

    [Fact]
    public async Task A_message_inherits_its_exchange_conversation()
    {
        // SPEC 5.0: the exchange holds the conversation and everything on it follows. A message
        // stores a copy, which the composite foreign key keeps in agreement.
        var exchangeId = await OpenInConversation("pricing-boundary", "where does price live");

        var question = await Append(
            type: MessageType.Question, exchangeId: exchangeId,
            payload: """{"text":"gross or net?","consequences":"If gross I stop adding tax at invoice time; if net I keep it."}""");
        var clarify = await Append(
            type: MessageType.Clarify, exchangeId: exchangeId, from: Billing, to: Orders,
            payload: """{"text":"gross or net of tax?"}""");

        Assert.Equal("pricing-boundary", question.Message.ConversationId);
        Assert.Equal("pricing-boundary", clarify.Message.ConversationId);

        var conversation = await _messages.GetConversationAsync(
            "pricing-boundary", TestContext.Current.CancellationToken);
        Assert.Equal(2, conversation.Count);
    }

    [Fact]
    public async Task A_reply_cannot_reassign_its_exchange_to_another_conversation()
    {
        // Without this a clarification could split one discussion across two exports, or graft
        // it onto somebody else's. The composite foreign key refuses it too; refusing here
        // means the caller learns what it did rather than meeting a constraint violation.
        var exchangeId = await OpenInConversation("pricing-boundary", "where does price live");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Append(
            type: MessageType.Clarify, exchangeId: exchangeId, from: Billing, to: Orders,
            payload: """{"text":"gross or net?"}""", conversationId: "invoice-rounding"));

        Assert.Equal("conversationId", ex.ParamName);
    }

    [Fact]
    public async Task An_exchange_that_began_alone_cannot_be_joined_to_a_conversation()
    {
        // SPEC 5.0 gives the choice to the question, which means a reply cannot retroactively
        // enrol an exchange that started without one. Pinned because the alternative reading —
        // that a null conversation is simply unset and free to fill in — is equally plausible
        // from the code, and the two differ in whether an agent can annexe an exchange after
        // the fact.
        await Append(type: MessageType.Question);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Append(
            type: MessageType.Clarify, from: Billing, to: Orders,
            payload: """{"text":"gross or net?"}""", conversationId: "pricing-boundary"));

        Assert.Equal("conversationId", ex.ParamName);
    }

    [Fact]
    public async Task A_reply_may_restate_the_conversation_it_already_belongs_to()
    {
        // Agreeing with the exchange is not a conflict, so a caller that threads the identifier
        // through every call is not punished for it.
        var exchangeId = await OpenInConversation("pricing-boundary", "where does price live");

        var clarify = await Append(
            type: MessageType.Clarify, exchangeId: exchangeId, from: Billing, to: Orders,
            payload: """{"text":"gross or net?"}""", conversationId: "pricing-boundary");

        Assert.Equal("pricing-boundary", clarify.Message.ConversationId);
    }

    [Theory]
    [InlineData("run 3")]
    [InlineData("run`3")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task An_identifier_that_could_travel_badly_is_rejected(string id)
    {
        // Agent-supplied, and it names an export, so it is likely to reach a filename, a report
        // heading or a console. Same restriction as responder_model, for the same reason.
        //
        // Checked on a Notify, which is now the one message type that declares its own
        // conversation. Everything attached to an exchange takes the exchange's.
        await Assert.ThrowsAsync<SqliteException>(() => Append(
            type: MessageType.Notify, exchangeId: null, to: Billing,
            payload: """{"n":"contract changed"}""", conversationId: id));
    }

    [Fact]
    public async Task Exporting_without_an_identifier_is_rejected()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _messages.GetConversationAsync("", TestContext.Current.CancellationToken));
    }

    // ---- replay ----

    [Fact]
    public async Task Replay_returns_a_message_to_the_inbox_without_writing_a_new_one()
    {
        // Replay is re-delivery, not re-sending. A fresh row would make the log a record of
        // delivery attempts rather than of what was said.
        var outcome = await Append();
        await _messages.MarkDeliveredAsync(
            outcome.Message.Id, T0.AddMinutes(1), TestContext.Current.CancellationToken);

        Assert.True(await _messages.RedeliverAsync(
            outcome.Message.Id, TestContext.Current.CancellationToken));

        var pending = await _messages.GetPendingAsync(Billing, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(pending);
        Assert.Equal(outcome.Message.Id, pending[0].Id);
        Assert.Null(pending[0].DeliveredUtc);
    }

    [Fact]
    public async Task Replay_long_after_the_window_still_writes_nothing_new()
    {
        // The reason re-delivery is not merely a convenience. Re-sending after a long outage
        // would fall outside the deduplication window, succeed, and double the record — which
        // is exactly the case a fast-restart dedup hit would have hidden.
        var outcome = await Append();
        await _messages.MarkDeliveredAsync(
            outcome.Message.Id, T0.AddMinutes(1), TestContext.Current.CancellationToken);

        await _messages.RedeliverAsync(outcome.Message.Id, TestContext.Current.CancellationToken);

        var all = await _messages.GetPendingAsync(Billing, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(all);
    }

    [Fact]
    public async Task Replaying_a_message_that_does_not_exist_reports_failure()
    {
        Assert.False(await _messages.RedeliverAsync(9999, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _messages.AppendAsync(
            _exchangeId, MessageType.Question, Orders, Billing, Model, Payload, T0,
            cancellationToken: cancelled.Token));
    }
}
