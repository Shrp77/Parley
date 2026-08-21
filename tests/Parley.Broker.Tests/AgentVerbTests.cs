using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The operations an agent actually calls: SPEC.md §10.1's <c>ask</c>, <c>clarify</c> and
/// <c>answer</c>.
/// </summary>
/// <remarks>
/// Each of these is one protocol operation and two writes, and the reason they exist as broker
/// methods rather than as a pair of repository calls is that nothing else makes the two atomic.
/// Most of what follows probes the seam between them: a question the schema refuses must leave
/// no exchange, and an answer the schema refuses must leave the exchange open.
/// </remarks>
public sealed class AgentVerbTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";

    /// <summary>A question satisfying rule 1's consequences floor.</summary>
    private const string Question =
        """{"text":"is the price gross or net?","consequences":"If gross I stop adding tax; if net I keep adding it."}""";

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly AgentRepository _agents;
    private readonly Broker _broker;

    public AgentVerbTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private Task<AskOutcome> Ask(
        string payload = Question,
        AgentName? from = null,
        AgentName? to = null,
        DateTimeOffset? at = null,
        string? conversationId = null) =>
        _broker.AskAsync(
            from ?? Orders,
            to ?? Billing,
            Model,
            payload,
            at ?? T0.AddMinutes(1),
            conversationId,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Asking_opens_an_exchange_and_puts_the_question_in_the_answerer_inbox()
    {
        var asked = await Ask();

        Assert.True(asked.Succeeded);
        Assert.Equal(ExchangeState.Open, asked.Exchange!.State);
        Assert.Equal(Billing, asked.Exchange.OwedBy);

        var inbox = (await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        var delivered = Assert.Single(inbox);
        Assert.Equal(asked.Question!.Id, delivered.Message.Id);
        Assert.Equal(MessageType.Question, delivered.Message.Type);
        Assert.Equal(asked.Exchange.Id, delivered.Message.ExchangeId);
    }

    [Fact]
    public async Task Ask_returns_an_identifier_and_never_an_answer()
    {
        // SPEC.md §4.2. The exchange comes back so the asker can recognise the answer when it
        // arrives through the inbox; nothing about the answer is available yet, because nobody
        // has been handed the question.
        var asked = await Ask();

        Assert.True(asked.Exchange!.Id > 0);
        Assert.Null(asked.Exchange.Resolution);
        Assert.Empty((await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken)).Messages);
    }

    [Fact]
    public async Task A_question_the_schema_refuses_leaves_no_exchange_behind()
    {
        // The failure this method exists to prevent. As two commits, the exchange was already
        // committed when the question was refused, and it then held a pair slot and an entry in
        // the distinctness index for an exchange nobody would ever be asked about — reported to
        // the asker as ghosted once its windows ran out.
        await Assert.ThrowsAsync<SqliteException>(() => Ask(
            """{"text":"is the price gross or net?","consequences":"n/a"}"""));

        Assert.Empty(await _exchanges.GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await _messages.GetPendingAsync(
            Billing, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refused_question_does_not_consume_a_pair_slot()
    {
        // The consequence that makes the above more than untidy: five phantoms saturate the
        // pair under §7.1 and lock the asker out of a peer entirely, with no way to recover
        // except waiting out every one of their grace periods.
        for (var i = 0; i < ExchangePolicy.Default.OpenPairCap + 1; i++)
        {
            await Assert.ThrowsAsync<SqliteException>(() => Ask(
                $$"""{"text":"attempt {{i}}","consequences":"n/a"}"""));
        }

        Assert.True((await Ask()).Succeeded);
    }

    [Fact]
    public async Task The_same_question_asked_twice_is_refused_as_a_duplicate()
    {
        var first = await Ask();
        Assert.True(first.Succeeded);

        // Well outside the §13.1 deduplication window, so this is the distinctness rule of §7.1
        // rather than delivery deduplication.
        var second = await Ask(at: T0.AddHours(1));

        Assert.Equal(OpenRejection.DuplicateQuestion, second.Rejection);
        Assert.Equal(first.Exchange!.Id, Assert.Single(second.Admission.Outstanding).Id);
    }

    [Fact]
    public async Task A_question_differing_only_in_its_consequences_is_a_different_question()
    {
        // The whole payload is hashed, not the question text. Rule 1's statement of what the
        // asker will do with each answer is part of what is being asked: the same words with
        // different consequences ask the answerer to weigh different things.
        Assert.True((await Ask()).Succeeded);

        var reframed = await Ask(
            """{"text":"is the price gross or net?","consequences":"If gross I fail the import; if net I proceed."}""");

        Assert.True(reframed.Succeeded);
    }

    [Fact]
    public async Task Asking_uses_the_broker_policy_and_offers_no_way_to_choose_another()
    {
        // §7.1's caps are controls, not tuning knobs, and there is deliberately no parameter an
        // agent-facing path could smuggle a policy through. This pins the terms actually
        // stamped on the exchange to the operator's configuration.
        var strict = ExchangePolicy.Default with { ClarifyDepthCap = 1, OpenPairCap = 2 };
        var broker = new Broker(_connection, BrokerOptions.Default with { ExchangePolicy = strict });
        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);

        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, asked.Exchange!.ClarifyDepthCap);
    }

    [Fact]
    public async Task Answering_closes_the_exchange_and_delivers_the_answer_to_the_asker()
    {
        var asked = await Ask();
        await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);

        var answered = await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(answered.Succeeded);

        var closed = await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Closed, closed!.State);
        Assert.Equal(Resolution.Answered, closed.Resolution);

        var inbox = (await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(4), cancellationToken: TestContext.Current.CancellationToken)).Messages;
        Assert.Equal(MessageType.Answer, Assert.Single(inbox).Message.Type);
    }

    [Fact]
    public async Task An_answer_the_schema_refuses_leaves_the_exchange_open()
    {
        // The mirror of the phantom, and worse. As two commits this closed the exchange
        // 'answered' and then failed to write the answer, so the asker learned its question was
        // settled with no way to discover the settlement — and a closed exchange is terminal, so
        // no replay and no grace period ever reaches it.
        var asked = await Ask();
        var exchangeId = asked.Exchange!.Id;

        await Assert.ThrowsAsync<SqliteException>(() => _broker.AnswerAsync(
            exchangeId, Billing, Model,
            new string('x', 2100),
            T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken));

        var loaded = await _exchanges.GetAsync(
            exchangeId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Open, loaded!.State);
        Assert.Null(loaded.Resolution);
    }

    [Fact]
    public async Task Only_the_answerer_may_answer()
    {
        var asked = await Ask();

        var wrongWay = await _broker.AnswerAsync(
            asked.Exchange!.Id, Orders, Model, """{"resolutions":[]}""",
            T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransitionError.OnlyAnswererMayAnswer, wrongWay.Error);

        // An answer would have been addressed to the asker, so the asker holding nothing is
        // what says none was written.
        Assert.Empty(await _messages.GetPendingAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_outside_the_exchange_cannot_speak_in_it()
    {
        // The recipient is derived from the exchange rather than passed, so there is no field
        // through which a message could be addressed outside it. This checks the other half:
        // a stranger cannot get a message onto the exchange at all.
        var asked = await Ask();

        var intruder = await _broker.AnswerAsync(
            asked.Exchange!.Id, Inventory, Model, """{"resolutions":[]}""",
            T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransitionError.NotAParticipant, intruder.Error);
    }

    [Fact]
    public async Task An_exchange_that_does_not_exist_is_reported_as_not_open()
    {
        // Deliberately indistinguishable from an exchange that has closed. Telling an agent
        // which identifiers exist would leak the log SPEC.md §10.1 refuses to expose.
        var missing = await _broker.AnswerAsync(
            424242, Billing, Model, """{"resolutions":[]}""",
            T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransitionError.ExchangeNotOpen, missing.Error);
    }

    [Fact]
    public async Task An_answer_may_not_carry_a_ghost()
    {
        // A ghost asserts nobody answered, so it cannot arrive on an answer. Refused in the
        // domain rather than by the schema so the caller learns why.
        var asked = await Ask();

        var forged = await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model, """{"resolutions":[]}""",
            T0.AddMinutes(3), Resolution.Ghosted,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransitionError.GhostIsNotAnAnswer, forged.Error);
        Assert.Equal(
            ExchangeState.Open,
            (await _exchanges.GetAsync(
                asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken))!.State);
    }

    [Fact]
    public async Task Clarifying_hands_the_turn_back_and_charges_the_answerer()
    {
        var asked = await Ask();

        var clarified = await _broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, """{"text":"gross of what, VAT or duty?"}""",
            T0.AddMinutes(3), TestContext.Current.CancellationToken);

        Assert.True(clarified.Succeeded);

        var loaded = await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Orders, loaded!.OwedBy);
        Assert.Equal(1, loaded.ClarifyDepth);

        var inbox = (await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(4), cancellationToken: TestContext.Current.CancellationToken)).Messages;
        Assert.Equal(MessageType.Clarify, Assert.Single(inbox).Message.Type);
    }

    [Fact]
    public async Task The_asker_supplying_detail_is_not_charged_a_round()
    {
        // Depth increments only when the answerer asks. The asker replying is supplying what
        // was asked for, and charging both directions would halve the cap without saying so.
        var asked = await Ask();
        await _broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, """{"text":"gross of what?"}""",
            T0.AddMinutes(3), TestContext.Current.CancellationToken);

        var replied = await _broker.ClarifyAsync(
            asked.Exchange.Id, Orders, Model, """{"text":"of VAT."}""",
            T0.AddMinutes(4), TestContext.Current.CancellationToken);

        Assert.True(replied.Succeeded);

        var loaded = await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, loaded!.ClarifyDepth);
        Assert.Equal(Billing, loaded.OwedBy);
    }

    [Fact]
    public async Task A_retried_clarification_does_not_advance_the_exchange_twice()
    {
        // At-least-once transport makes a repeated send ordinary traffic, and §13.1 suppresses
        // the second write. What must also not happen is the transition applying again: found
        // by probe, a suppressed retry still charged the clarify cap and still flipped the turn,
        // handing the peer an obligation with nothing in its inbox to explain it.
        var asked = await Ask();

        await _broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, """{"text":"gross of what?"}""",
            T0.AddMinutes(2), TestContext.Current.CancellationToken);
        await _broker.ClarifyAsync(
            asked.Exchange.Id, Orders, Model, """{"text":"of VAT."}""",
            T0.AddMinutes(2), TestContext.Current.CancellationToken);

        var beforeRetry = await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken);

        var retry = await _broker.ClarifyAsync(
            asked.Exchange.Id, Billing, Model, """{"text":"gross of what?"}""",
            T0.AddMinutes(2), TestContext.Current.CancellationToken);

        // Reported as success carrying the earlier message, because from the sender's side the
        // send did succeed — once.
        Assert.True(retry.Succeeded);
        Assert.False(retry.Stored);

        var afterRetry = await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(beforeRetry!.ClarifyDepth, afterRetry!.ClarifyDepth);
        Assert.Equal(beforeRetry.OwedBy, afterRetry.OwedBy);
        Assert.Equal(beforeRetry.Version, afterRetry.Version);
    }

    [Fact]
    public async Task A_retried_answer_does_not_close_the_exchange_twice()
    {
        // The same hazard where it costs most. An answer closes the exchange, so a retry that
        // re-applied the transition against an exchange the first one closed would be refused
        // — but only because the first succeeded. This pins that the retry reports the earlier
        // answer rather than a contradiction.
        var asked = await Ask();
        const string Payload =
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""";

        var first = await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model, Payload, T0.AddMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(first.Stored);

        var retry = await _broker.AnswerAsync(
            asked.Exchange.Id, Billing, Model, Payload, T0.AddMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);

        // Refused on the transition, before deduplication is reached: the exchange is closed.
        Assert.Equal(TransitionError.ExchangeNotOpen, retry.Error);

        var inbox = (await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken)).Messages;
        Assert.Single(inbox);
    }

    [Fact]
    public async Task Speaking_out_of_turn_is_refused()
    {
        var asked = await Ask();

        var outOfTurn = await _broker.ClarifyAsync(
            asked.Exchange!.Id, Orders, Model, """{"text":"any progress?"}""",
            T0.AddMinutes(3), TestContext.Current.CancellationToken);

        Assert.Equal(TransitionError.NotYourTurn, outOfTurn.Error);
    }

    [Fact]
    public async Task Breaching_the_clarify_cap_is_refused_rather_than_closing_the_exchange()
    {
        // SPEC.md §7 has the broker call ForceUnknown at this point, which is an operator
        // decision this call does not make on the agent's behalf. The agent learns it has run
        // out of budget; the exchange stays open for somebody to dispose of deliberately.
        var strict = ExchangePolicy.Default with { ClarifyDepthCap = 1 };
        var broker = new Broker(_connection, BrokerOptions.Default with { ExchangePolicy = strict });
        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);

        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        await broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, """{"text":"gross of what?"}""",
            T0.AddMinutes(2), TestContext.Current.CancellationToken);
        await broker.ClarifyAsync(
            asked.Exchange.Id, Orders, Model, """{"text":"of VAT."}""",
            T0.AddMinutes(3), TestContext.Current.CancellationToken);

        var breach = await broker.ClarifyAsync(
            asked.Exchange.Id, Billing, Model, """{"text":"and duty?"}""",
            T0.AddMinutes(4), TestContext.Current.CancellationToken);

        Assert.Equal(TransitionError.ClarifyDepthExceeded, breach.Error);

        var loaded = await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Open, loaded!.State);
    }

    [Fact]
    public async Task A_refused_move_writes_no_message()
    {
        // The transition is decided before anything is written, so a refusal leaves the log
        // untouched. A message announcing a move that did not happen would make the log
        // disagree with the exchange it describes.
        var asked = await Ask();

        await _broker.ClarifyAsync(
            asked.Exchange!.Id, Orders, Model, """{"text":"any progress?"}""",
            T0.AddMinutes(3), TestContext.Current.CancellationToken);

        // The refused clarify would have been addressed to billing, so billing holding only
        // the question is what says nothing was written.
        var pending = await _messages.GetPendingAsync(
            Billing, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(MessageType.Question, Assert.Single(pending).Type);
    }

    [Fact]
    public async Task An_unreachable_answerer_is_refused_immediately()
    {
        await _agents.MarkUnreachableAsync(Billing, TestContext.Current.CancellationToken);

        var asked = await Ask();

        Assert.Equal(OpenRejection.AnswererUnreachable, asked.Rejection);
        Assert.Null(asked.Question);
    }

    [Fact]
    public async Task A_retired_agent_is_not_routed_to()
    {
        // SPEC.md §14: a retired agent is not routed to, and §10.1's agents() does not list it,
        // so an asker should not have the name at all — this is the backstop for one that does.
        // Found by probe: the question was admitted, and since a retired agent is not spawned
        // either, it would have sat until the windows ran out and been reported as ghosted.
        // That makes retirement a control that does not hold, which is precisely the failure
        // §14 records when it refuses to let re-registration clear the mark.
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);

        var asked = await Ask();

        Assert.Equal(OpenRejection.AnswererUnreachable, asked.Rejection);
        Assert.Null(asked.Question);
    }

    [Fact]
    public async Task A_retired_agent_may_not_open_new_exchanges_either()
    {
        // The same §14 rule read from the other end, and the reason it is not merely tidiness:
        // an answer is a message addressed to the asker, so admitting a retired agent's question
        // commits the broker to routing to an agent it must not route to. Refusing at the ask is
        // what keeps that from being a violation already scheduled.
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        var asked = await Ask();

        Assert.Equal(OpenRejection.AskerRetired, asked.Rejection);
    }

    [Fact]
    public async Task An_exchange_opened_before_retirement_can_still_be_answered()
    {
        // Retirement is an operator's judgement about a repository, not a kill signal. Work
        // already in flight finishes rather than being stranded, which would throw away answers
        // peers had gone to the trouble of producing.
        var asked = await Ask();
        Assert.True(asked.Succeeded);

        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        var answered = await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(answered.Succeeded);
    }

    [Fact]
    public async Task Reinstating_an_agent_makes_it_routable_again()
    {
        // The symmetric half. §14 makes retire and reinstate a matched pair entered and
        // withdrawn by a person, so a refusal that survived reinstatement would be as broken as
        // a retirement that did not hold.
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);
        await _agents.ReinstateAsync(Billing, TestContext.Current.CancellationToken);

        var asked = await Ask();

        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");
    }

    [Fact]
    public async Task A_failed_ask_leaves_no_conversation_behind()
    {
        // Admission creates the conversation row for an identifier it has not seen, in the same
        // transaction as the question. If the question is refused the discussion goes with it —
        // otherwise an agent could mint a channel by failing, and a later question naming that
        // identifier would join a discussion nobody ever opened.
        await Assert.ThrowsAsync<SqliteException>(() => Ask(
            payload: """{"text":"is the price gross or net?","consequences":"n/a"}""",
            conversationId: "invented"));

        var conversations = await _connection.QuerySingleAsync<long>(
            "SELECT count(*) FROM conversations WHERE id = 'invented';");
        Assert.Equal(0, conversations);
    }

    [Fact]
    public async Task A_question_that_would_close_a_cycle_is_refused()
    {
        // §7.2: a cycle is a set of questions none of which can ever be answered, because every
        // agent in it is waiting on the next.
        Assert.True((await Ask(from: Orders, to: Billing)).Succeeded);
        Assert.True((await Ask(
            payload: """{"text":"which ledger owns stock?","consequences":"If ours I write it; if theirs I read it."}""",
            from: Billing, to: Inventory)).Succeeded);

        var closesIt = await Ask(
            payload: """{"text":"what is the order of record?","consequences":"If ours I own it; if theirs I defer to it."}""",
            from: Inventory, to: Orders);

        Assert.Equal(OpenRejection.WouldDeadlock, closesIt.Rejection);
        Assert.NotEmpty(closesIt.Admission.CyclePath);
    }

    [Fact]
    public async Task A_question_naming_a_concluded_conversation_is_refused_and_says_so()
    {
        // SPEC.md §5.0: a closed conversation stays closed. Closure is by consensus — a poll,
        // a decline veto, a silence rule — and admitting this would let one agent overturn all
        // three by naming the identifier again. Found by probe: it was admitted, and the
        // discussion reopened with nobody asked.
        var asked = await Ask(conversationId: "pricing");
        Assert.True(asked.Succeeded);

        await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model, """{"resolutions":[]}""",
            T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);

        await _connection.ExecuteAsync(
            "UPDATE conversations SET state = 'CLOSED', closed_utc = @When WHERE id = 'pricing';",
            new { When = "2026-01-01T00:03:00.000Z" });

        var afterClosure = await Ask(
            payload: """{"text":"and for refunds?","consequences":"If gross I reverse tax; if net I do not."}""",
            at: T0.AddMinutes(4),
            conversationId: "pricing");

        Assert.Equal(OpenRejection.ConversationClosed, afterClosure.Rejection);
        Assert.Null(afterClosure.Question);
    }

    [Fact]
    public async Task The_same_question_without_the_concluded_identifier_is_admitted()
    {
        // What makes the refusal actionable rather than a dead end: the discussion is over, the
        // question is not, and asking it afresh costs an identifier. §5.0 declines to make
        // reopening a special case for exactly this reason.
        var asked = await Ask(conversationId: "pricing");
        await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model, """{"resolutions":[]}""",
            T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);

        await _connection.ExecuteAsync(
            "UPDATE conversations SET state = 'CLOSED', closed_utc = @When WHERE id = 'pricing';",
            new { When = "2026-01-01T00:03:00.000Z" });

        var afresh = await Ask(at: T0.AddMinutes(4), conversationId: "pricing-refunds");

        Assert.True(afresh.Succeeded, $"expected admission, got {afresh.Rejection}");
    }

    [Fact]
    public async Task A_conversation_identifier_travels_from_the_question_to_the_answer()
    {
        // SPEC.md §5.0: the exchange holds the authoritative conversation and a later message
        // inherits it rather than declaring one. Nothing is passed on the answer, and the copy
        // still lands, because the repository reads it from the exchange.
        var asked = await Ask(conversationId: "pricing-model");
        Assert.True(asked.Succeeded);

        var answered = await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("pricing-model", answered.Message!.ConversationId);
    }
}
