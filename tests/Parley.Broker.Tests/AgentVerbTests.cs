using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The operations a participant actually calls: SPEC.md §10.1's <c>request</c>, <c>clarify</c> and
/// <c>result</c>.
/// </summary>
/// <remarks>
/// Each of these is one protocol operation and two writes, and the reason they exist as broker methods rather
/// than as a pair of repository calls is that nothing else makes the two atomic. Most of what follows probes
/// the seam between them: a Request the schema refuses must leave no exchange, and a Result the schema
/// refuses must leave the exchange open.
/// </remarks>
public sealed class AgentVerbTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = Requests.Model;

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly AgentRepository _agents;
    private readonly Broker _broker;

    public AgentVerbTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing, Inventory);

        _agents = new AgentRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<RequestOutcome> Send(
        string? payload = null,
        AgentName? from = null,
        AgentName? to = null,
        DateTimeOffset? at = null,
        string? conversationId = null,
        Broker? broker = null) =>
        (broker ?? _broker).RequestAsync(
            from ?? Orders, to ?? Billing, Model, payload ?? Requests.Request(), at ?? T0.AddMinutes(1), conversationId, Ct);

    private Task<SayOutcome> Result(long exchangeId, string? payload = null, AgentName? from = null, int minute = 3) =>
        _broker.ResultAsync(exchangeId, from ?? Billing, Model, payload ?? Requests.Result(), T0.AddMinutes(minute), Ct);

    private Task<SayOutcome> Clarify(long exchangeId, AgentName from, string text, int minute, Broker? broker = null) =>
        (broker ?? _broker).ClarifyAsync(exchangeId, from, Model, Requests.Clarify(text), T0.AddMinutes(minute), Ct);

    // ---- request ----

    [Fact]
    public async Task A_request_opens_an_exchange_and_puts_the_request_in_the_receivers_inbox()
    {
        var sent = await Send();

        Assert.True(sent.Succeeded);
        Assert.Equal(ExchangeState.Open, sent.Exchange!.State);
        Assert.Equal(Billing, sent.Exchange.OwedBy);

        var delivered = Assert.Single((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct)).Messages);

        Assert.Equal(sent.Request!.Id, delivered.Message.Id);
        Assert.Equal(MessageType.Request, delivered.Message.Type);
        Assert.Equal(sent.Exchange.Id, delivered.Message.ExchangeId);
    }

    [Fact]
    public async Task A_request_returns_an_identifier_and_its_conversation_and_never_a_result()
    {
        // SPEC.md §4.2. The exchange comes back so the requester can recognise the Result when it arrives
        // through the inbox; nothing about the Result is available yet.
        var sent = await Send();

        Assert.True(sent.Exchange!.Id > 0);
        Assert.Null(sent.Exchange.Outcome);
        Assert.StartsWith("c-", sent.Request!.ConversationId, StringComparison.Ordinal);
        Assert.Empty((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(2), cancellationToken: Ct)).Messages);
    }

    [Fact]
    public async Task A_request_the_schema_refuses_leaves_nothing_behind()
    {
        // The failure this method exists to prevent. As two commits, the exchange would be committed when the
        // Request was refused, and the person would be shown work waiting on a receiver never asked — in a
        // conversation minted by failing.
        await Assert.ThrowsAsync<SqliteException>(() => Send(Requests.Request("   "), conversationId: "invented"));

        Assert.Empty(await _exchanges.GetOpenAsync(cancellationToken: Ct));
        Assert.Empty(await _messages.GetPendingAsync(Billing, cancellationToken: Ct));
        Assert.Equal(0, await _connection.QuerySingleAsync<long>("SELECT count(*) FROM conversations WHERE id = 'invented';"));
    }

    [Fact]
    public async Task A_refused_request_does_not_consume_a_pair_slot()
    {
        // The cap is set here rather than taken from the default, which is off: against no cap the closing
        // assertion would pass whether or not a refused Request consumed a slot.
        const int cap = 3;

        var broker = new Broker(
            _connection, BrokerOptions.Default with { ExchangePolicy = ExchangePolicy.Default with { OpenPairCap = cap } });
        await broker.StartAsync(Orders, T0, Ct);

        for (var i = 0; i < cap + 1; i++)
        {
            await Assert.ThrowsAsync<SqliteException>(() => Send(Requests.Request(string.Empty), broker: broker));
        }

        var admitted = await Send(broker: broker);

        Assert.True(admitted.Succeeded, $"expected admission, got {admitted.Rejection}");
    }

    [Fact]
    public async Task The_same_request_sent_twice_is_refused_as_a_duplicate()
    {
        var first = await Send();

        // Well outside the §13.1 deduplication window, so this is the distinctness rule of §7.1.
        var second = await Send(at: T0.AddHours(1));

        Assert.Equal(RequestRejection.Duplicate, second.Rejection);
        Assert.Equal(first.Exchange!.Id, Assert.Single(second.Admission.Outstanding).Id);
    }

    [Fact]
    public async Task A_request_whose_ask_differs_is_a_different_request()
    {
        // The whole payload is hashed, which since 0.7 is the ask. Different words are a different piece of work,
        // and the distinctness rule of §7.1 admits the second.
        Assert.True((await Send()).Succeeded);

        Assert.True((await Send(Requests.Request("does the import proceed or fail with a reason?"))).Succeeded);
    }

    [Fact]
    public async Task A_request_uses_the_broker_policy_and_offers_no_way_to_choose_another()
    {
        // §7.1's caps are controls, not tuning knobs, and there is deliberately no parameter an agent-facing
        // path could smuggle a policy through.
        var strict = ExchangePolicy.Default with { ClarifyDepthCap = 1, OpenPairCap = 2 };
        var broker = new Broker(_connection, BrokerOptions.Default with { ExchangePolicy = strict });
        await broker.StartAsync(Orders, T0, Ct);

        Assert.Equal(1, (await Send(broker: broker)).Exchange!.ClarifyDepthCap);
    }

    [Fact]
    public async Task A_request_that_closes_a_cycle_is_admitted_and_recorded()
    {
        // §7.2 in 0.5: agents asking each other for work in a loop is ordinary, and nothing waits
        // synchronously, so nothing is deadlocked. The loop is recorded where the person can see it stall.
        Assert.True((await Send(from: Orders, to: Billing)).Succeeded);

        var billingHolds = await _exchanges.GetHeldConversationsAsync(Billing, cancellationToken: Ct);
        Assert.True((await Send(Requests.Request("which ledger owns stock?"), from: Billing, to: Inventory)).Succeeded);

        var closesIt = await Send(Requests.Request("what is the order of record?"), from: Inventory, to: Orders);

        Assert.True(closesIt.Succeeded, $"expected admission, got {closesIt.Rejection}");
        Assert.Equal([Inventory, Orders, Billing], closesIt.Admission.CyclePath);
        Assert.Single(billingHolds);
    }

    // ---- conversation assignment (§5.0) ----

    [Fact]
    public async Task A_receiver_asking_somebody_else_for_part_of_the_work_stays_in_the_conversation()
    {
        // The rule the assignment exists for: billing, working on orders' Request, cannot split the line of
        // work by naming nothing.
        var sent = await Send(conversationId: "pricing");

        var onward = await Send(Requests.Request("how much stock?"), from: Billing, to: Inventory);

        Assert.Equal("pricing", onward.Request!.ConversationId);
        Assert.Equal(sent.Request!.ConversationId, onward.Request.ConversationId);
    }

    [Fact]
    public async Task A_receiver_cannot_start_a_new_conversation_while_it_holds_work_and_is_told_which_it_holds()
    {
        await Send(conversationId: "pricing");

        var elsewhere = await Send(Requests.Request("how much stock?"), from: Billing, to: Inventory, conversationId: "somewhere-else");

        Assert.Equal(RequestRejection.ConversationNotAssignable, elsewhere.Rejection);
        Assert.Equal(["pricing"], elsewhere.Admission.Assignable);
    }

    [Fact]
    public async Task A_person_may_start_a_conversation_whatever_it_holds()
    {
        // Principle 2.10: a person is where lines of work begin.
        var person = AgentName.Create("arvind@test");
        Requests.Register(_connection, person, PersonSeat.Adapter);

        await Send(from: Orders, to: person, conversationId: "pricing");

        var started = await Send(Requests.Request("start the refunds work"), from: person, to: Inventory, conversationId: "refunds");

        Assert.True(started.Succeeded, $"expected admission, got {started.Rejection}");
        Assert.Equal("refunds", started.Request!.ConversationId);
    }

    [Fact]
    public async Task A_request_naming_a_closed_conversation_is_refused_and_says_who_closed_it()
    {
        // A closed conversation stays closed: admitting this would let one participant overturn a consensus,
        // or a person's decision, by naming the identifier again.
        Assert.True((await Send(conversationId: "pricing")).Succeeded);
        await new ConversationRepository(_connection).ForceConcludeAsync("pricing", "done with it", T0.AddMinutes(2), cancellationToken: Ct);

        var afterClosure = await Send(Requests.Request("and for refunds?"), at: T0.AddMinutes(4), conversationId: "pricing");

        Assert.Equal(RequestRejection.ConversationClosed, afterClosure.Rejection);
        Assert.Equal(ConversationCloser.Person, afterClosure.Admission.ClosedBy);
        Assert.Null(afterClosure.Request);
    }

    [Fact]
    public async Task The_same_work_in_a_new_conversation_is_admitted()
    {
        // What makes the refusal actionable rather than a dead end: the conversation is over, the work is not.
        await Send(conversationId: "pricing");
        await new ConversationRepository(_connection).ForceConcludeAsync("pricing", null, T0.AddMinutes(2), cancellationToken: Ct);

        var afresh = await Send(at: T0.AddMinutes(4), conversationId: "pricing-refunds");

        Assert.True(afresh.Succeeded, $"expected admission, got {afresh.Rejection}");
    }

    // ---- result ----

    [Fact]
    public async Task A_result_closes_the_exchange_and_reaches_the_requester()
    {
        var sent = await Send();
        await _broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct);

        Assert.True((await Result(sent.Exchange!.Id)).Succeeded);

        var closed = await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct);
        Assert.Equal(ExchangeState.Closed, closed!.State);
        Assert.Equal(ExchangeOutcome.Complete, closed.Outcome);

        var inbox = (await _broker.DrainInboxAsync(Orders, T0.AddMinutes(4), cancellationToken: Ct)).Messages;
        Assert.Equal(MessageType.Result, Assert.Single(inbox).Message.Type);
    }

    [Fact]
    public async Task The_rolled_up_outcome_is_derived_from_the_result_and_not_supplied()
    {
        // A sender reporting blocked under an exchange recorded complete would make §15 describe a deployment that
        // does not exist.
        var sent = await Send();

        Assert.True((await Result(
            sent.Exchange!.Id,
            Requests.Result((PayloadShape.WholeRequest, "blocked:missing_information")))).Succeeded);

        Assert.Equal(ExchangeOutcome.Blocked, (await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct))!.Outcome);
    }

    [Fact]
    public async Task A_result_answers_the_ask_and_is_told_so_when_it_names_something_else()
    {
        // A Request is answered whole (§5.3), under the identifier the protocol already used for one carrying no
        // items a Result could name. A Result naming anything else is refused and told what to send.
        var sent = await Send();

        var elsewhere = await Result(sent.Exchange!.Id, Requests.Result(("q1", "done")));

        Assert.Equal(TransitionError.OutcomesDoNotMatchItems, elsewhere.Error);
        Assert.Equal([PayloadShape.WholeRequest], elsewhere.ExpectedItems);
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct))!.State);
    }

    [Theory]
    [InlineData("""{"text":"net"}""")]
    [InlineData("""{"outcomes":[]}""")]
    [InlineData("""{"outcomes":[{"id":"q1","outcome":"out_of_scope","text":"x"}]}""")]
    public async Task A_result_with_no_readable_outcomes_is_refused_as_an_argument(string payload)
    {
        var sent = await Send();

        await Assert.ThrowsAsync<ArgumentException>(() => Result(sent.Exchange!.Id, payload));
    }

    [Fact]
    public async Task A_participant_cannot_record_closed_on_its_own_work()
    {
        // closed says a person or a consensus ended it. An agent writing it over work it did not do would make
        // abandoned work read as ended.
        var sent = await Send();

        var forged = await Result(sent.Exchange!.Id, Requests.Result((PayloadShape.WholeRequest, "closed")));

        Assert.Equal(TransitionError.ClosedIsTheBrokers, forged.Error);
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct))!.State);
    }

    [Fact]
    public async Task A_result_the_schema_refuses_leaves_the_exchange_open()
    {
        // The mirror of the phantom, and worse: as two commits this would close the exchange and fail to write
        // the Result, so the requester learned its work was settled with no way to discover how.
        var sent = await Send();

        var tooLong = $$"""{"outcomes":[{"id":"request","outcome":"answered","text":"{{new string('x', MessageLimits.MaxPayloadCharacters)}}"}]}""";

        await Assert.ThrowsAsync<SqliteException>(() => Result(sent.Exchange!.Id, tooLong));

        var loaded = await _exchanges.GetAsync(sent.Exchange!.Id, cancellationToken: Ct);
        Assert.Equal(ExchangeState.Open, loaded!.State);
        Assert.Null(loaded.Outcome);
    }

    [Fact]
    public async Task Only_the_receiver_sends_the_result()
    {
        var sent = await Send();

        var wrongWay = await Result(sent.Exchange!.Id, from: Orders);

        Assert.Equal(TransitionError.OnlyReceiverSendsResult, wrongWay.Error);
        Assert.Empty(await _messages.GetPendingAsync(Billing, cancellationToken: Ct) is var pending && pending.Count == 1 ? [] : pending);
    }

    [Fact]
    public async Task A_participant_outside_the_exchange_cannot_speak_in_it()
    {
        var sent = await Send();

        Assert.Equal(TransitionError.NotAParticipant, (await Result(sent.Exchange!.Id, from: Inventory)).Error);
    }

    [Fact]
    public async Task An_exchange_that_does_not_exist_is_reported_as_not_open()
    {
        // Deliberately indistinguishable from an exchange that has closed, so identifiers cannot be probed for.
        Assert.Equal(TransitionError.ExchangeNotOpen, (await Result(424242)).Error);
    }

    [Fact]
    public async Task A_retried_result_does_not_close_the_exchange_twice()
    {
        var sent = await Send();

        Assert.True((await Result(sent.Exchange!.Id, minute: 2)).Stored);

        var retry = await Result(sent.Exchange.Id, minute: 2);

        Assert.Equal(TransitionError.ExchangeNotOpen, retry.Error);
        Assert.Single((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: Ct)).Messages);
    }

    [Fact]
    public async Task The_conversation_travels_from_the_request_to_the_result()
    {
        var sent = await Send(conversationId: "pricing-model");

        Assert.Equal("pricing-model", (await Result(sent.Exchange!.Id)).Message!.ConversationId);
    }

    // ---- clarify ----

    [Fact]
    public async Task Clarifying_hands_the_turn_back_and_charges_the_receiver()
    {
        var sent = await Send();

        Assert.True((await Clarify(sent.Exchange!.Id, Billing, "gross of what, VAT or duty?", 3)).Succeeded);

        var loaded = await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct);
        Assert.Equal(Orders, loaded!.OwedBy);
        Assert.Equal(1, loaded.ClarifyDepth);

        var inbox = (await _broker.DrainInboxAsync(Orders, T0.AddMinutes(4), cancellationToken: Ct)).Messages;
        Assert.Equal(MessageType.Clarify, Assert.Single(inbox).Message.Type);
    }

    [Fact]
    public async Task The_requester_supplying_detail_is_not_charged_a_round()
    {
        var sent = await Send();
        await Clarify(sent.Exchange!.Id, Billing, "gross of what?", 3);

        Assert.True((await Clarify(sent.Exchange.Id, Orders, "of VAT.", 4)).Succeeded);

        var loaded = await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct);
        Assert.Equal(1, loaded!.ClarifyDepth);
        Assert.Equal(Billing, loaded.OwedBy);
    }

    [Fact]
    public async Task A_retried_clarification_does_not_advance_the_exchange_twice()
    {
        // At-least-once transport makes a repeated send ordinary traffic. A retry arrives while the move it
        // repeats has already handed the turn over, so the turn refuses it and nothing applies twice.
        var sent = await Send();

        await Clarify(sent.Exchange!.Id, Billing, "gross of what?", 2);

        var before = await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct);

        var retry = await Clarify(sent.Exchange.Id, Billing, "gross of what?", 2);

        Assert.Equal(TransitionError.NotYourTurn, retry.Error);

        var after = await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct);
        Assert.Equal(before!.ClarifyDepth, after!.ClarifyDepth);
        Assert.Equal(before.OwedBy, after.OwedBy);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task The_same_reply_to_a_second_clarification_hands_the_turn_back()
    {
        // Found by the review gate. This test once asserted the opposite, calling a clarification repeated
        // after its reply a retry. It is a participant saying the same thing at a later point, and a reply
        // of "yes" to two questions inside a minute was declined as a duplicate of the first, reported as
        // sent, and left the turn where it was — with nothing to time the exchange out.
        var sent = await Send();

        await Clarify(sent.Exchange!.Id, Billing, "Which invoice run?", 2);
        await Clarify(sent.Exchange.Id, Orders, "yes", 2);
        await Clarify(sent.Exchange.Id, Billing, "Including tax?", 2);

        var second = await Clarify(sent.Exchange.Id, Orders, "yes", 2);

        Assert.True(second.Succeeded);
        Assert.True(second.Stored);

        var loaded = await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct);
        Assert.Equal(Billing, loaded!.OwedBy);
        Assert.Equal(2, loaded.ClarifyDepth);
    }

    [Fact]
    public async Task Speaking_out_of_turn_is_refused_and_writes_nothing()
    {
        var sent = await Send();

        Assert.Equal(TransitionError.NotYourTurn, (await Clarify(sent.Exchange!.Id, Orders, "any progress?", 3)).Error);

        // The refused clarify would have been addressed to billing, so billing holding only the Request is what
        // says nothing was written.
        Assert.Equal(MessageType.Request, Assert.Single(await _messages.GetPendingAsync(Billing, cancellationToken: Ct)).Type);
    }

    [Fact]
    public async Task Breaching_a_set_clarify_cap_is_refused_rather_than_closing_the_exchange()
    {
        // The receiver learns it has run out of rounds and sends a Result with what it has; nothing closes the
        // exchange on its behalf.
        var broker = new Broker(_connection, BrokerOptions.Default with { ExchangePolicy = ExchangePolicy.Default with { ClarifyDepthCap = 1 } });
        await broker.StartAsync(Orders, T0, Ct);

        var sent = await Send(broker: broker);

        await Clarify(sent.Exchange!.Id, Billing, "gross of what?", 2, broker);
        await Clarify(sent.Exchange.Id, Orders, "of VAT.", 3, broker);

        Assert.Equal(TransitionError.ClarifyDepthExceeded, (await Clarify(sent.Exchange.Id, Billing, "and duty?", 4, broker)).Error);
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct))!.State);
    }

    // ---- retirement ----

    [Fact]
    public async Task A_retired_receiver_is_refused_immediately()
    {
        // Retirement rather than silence. Silence means only that nobody has that repository open, which is
        // where most agents are most of the time; retirement is the person's judgement that refuses.
        await _agents.RetireAsync(Billing, Ct);

        var sent = await Send();

        Assert.Equal(RequestRejection.ReceiverRetired, sent.Rejection);
        Assert.Null(sent.Request);
    }

    [Fact]
    public async Task A_retired_agent_may_not_send_new_requests_either()
    {
        await _agents.RetireAsync(Orders, Ct);

        Assert.Equal(RequestRejection.SenderRetired, (await Send()).Rejection);
    }

    [Fact]
    public async Task Work_sent_before_retirement_can_still_be_finished()
    {
        // Retirement is a judgement about a repository, not a kill signal. Work already in flight finishes.
        var sent = await Send();

        await _agents.RetireAsync(Orders, Ct);

        Assert.True((await Result(sent.Exchange!.Id)).Succeeded);
    }

    [Fact]
    public async Task Reinstating_an_agent_makes_it_routable_again()
    {
        await _agents.RetireAsync(Billing, Ct);
        await _agents.ReinstateAsync(Billing, Ct);

        var sent = await Send();

        Assert.True(sent.Succeeded, $"expected admission, got {sent.Rejection}");
    }
}
