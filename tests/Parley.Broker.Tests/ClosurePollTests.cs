using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The closure poll of SPEC.md §5.0: how a conversation ends by consensus.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "we concluded" and "nobody spoke for a while" are different facts, and a
/// log recording only the second cannot distinguish a discussion that reached an answer from
/// one that was abandoned. §15's <c>exchanges per promoted decision</c> is only meaningful if
/// that distinction survives.
/// </para>
/// <para>
/// Most of what follows is about what the poll refuses to disclose. §5.0 makes it strictly
/// bilateral — each participant is asked whether it is finished and told nothing about who else
/// is in the conversation, how many there are, or how anyone answered — because membership is
/// exactly the kind of thing an agent should not learn.
/// </para>
/// </remarks>
public sealed class ClosurePollTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";
    private const string Conversation = "pricing";

    private static readonly TimeSpan Silence = ExchangePolicy.Default.GracePeriod;

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ConversationRepository _conversations;
    private readonly Broker _broker;

    public ClosurePollTests()
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

        _conversations = new ConversationRepository(_connection);
        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(T0, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static string Question(string text) =>
        $$"""{"text":"{{text}}","consequences":"If yes I drop the column; if no I add a snapshot field."}""";

    private Task<AskOutcome> Ask(
        AgentName from,
        AgentName to,
        string text = "gross or net?",
        string? conversationId = Conversation,
        DateTimeOffset? at = null) =>
        _broker.AskAsync(
            from, to, Model, Question(text), at ?? T0.AddMinutes(1), conversationId,
            TestContext.Current.CancellationToken);

    private Task<ClosureOutcome> Conclude(AgentName agent, bool finished, DateTimeOffset? at = null) =>
        _broker.ConcludeAsync(
            Conversation, agent, finished, at ?? T0.AddMinutes(5),
            TestContext.Current.CancellationToken);

    private async Task<ConversationState> StateAsync() =>
        (await _conversations.GetAsync(
            Conversation, cancellationToken: TestContext.Current.CancellationToken))!.State;

    [Fact]
    public async Task Being_party_to_an_exchange_is_what_makes_an_agent_a_participant()
    {
        // Membership is derived, not declared: §5.0 has no join operation, because a
        // conversation is not a thing to be set up before use. Before this, the participants
        // table existed and nothing ever wrote to it, so the poll had no roster at all.
        Assert.True((await Ask(Orders, Billing)).Succeeded);

        Assert.True(await _conversations.IsParticipantAsync(
            Conversation, Orders, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await _conversations.IsParticipantAsync(
            Conversation, Billing, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await _conversations.IsParticipantAsync(
            Conversation, Inventory, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Membership_grows_and_the_conversation_stays_pairwise()
    {
        // §5.0's worked example: orders asks billing, orders asks inventory under the same
        // identifier. Three participants, one conversation, and still only pairwise traffic.
        Assert.True((await Ask(Orders, Billing)).Succeeded);
        Assert.True((await Ask(Orders, Inventory, "which ledger owns stock?")).Succeeded);

        foreach (var agent in new[] { Orders, Billing, Inventory })
        {
            Assert.True(await _conversations.IsParticipantAsync(
                Conversation, agent, cancellationToken: TestContext.Current.CancellationToken));
        }

        // Billing's inbox holds its own question and nothing of inventory's.
        var billingInbox = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(billingInbox.Messages);
    }

    [Fact]
    public async Task A_proposal_leaves_the_conversation_open_until_the_others_answer()
    {
        await Ask(Orders, Billing);

        var proposed = await Conclude(Orders, finished: true);

        Assert.True(proposed.Succeeded);
        Assert.False(proposed.Concluded);
        Assert.Equal(ConversationState.Closing, await StateAsync());
    }

    [Fact]
    public async Task Closure_is_reached_when_every_polled_participant_agrees()
    {
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);

        var agreed = await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.True(agreed.Concluded);
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task A_decline_blocks_and_is_not_outvoted()
    {
        // The rule that keeps this from being majority rule. §5.0: a majority that can conclude
        // over an objection is authority over another agent by a different name.
        await Ask(Orders, Billing);
        await Ask(Orders, Inventory, "which ledger owns stock?");

        await Conclude(Orders, finished: true);
        await Conclude(Inventory, finished: true, at: T0.AddMinutes(6));
        var declined = await Conclude(Billing, finished: false, at: T0.AddMinutes(7));

        Assert.False(declined.Concluded);

        // And it keeps blocking: two of three agreeing changes nothing, however long it stands.
        await _broker.TickAsync(T0.AddDays(1), TestContext.Current.CancellationToken);
        Assert.Equal(ConversationState.Closing, await StateAsync());
    }

    [Fact]
    public async Task A_participant_that_declined_may_change_its_mind()
    {
        // The vote is durable, not final. §5.0 has the broker re-ask when the conversation next
        // falls idle, which makes reversal the ordinary path rather than an edge case.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));

        var satisfied = await Conclude(Billing, finished: true, at: T0.AddMinutes(7));

        Assert.True(satisfied.Concluded);
    }

    [Fact]
    public async Task Silence_assents_once_the_window_has_elapsed()
    {
        // Without this, one unreachable agent could hold a discussion open indefinitely — which
        // would give an absent agent more power than a present one.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);

        var early = await _broker.TickAsync(
            T0.AddMinutes(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, early.ConversationsConcluded);
        Assert.Equal(ConversationState.Closing, await StateAsync());

        var late = await _broker.TickAsync(
            T0.AddMinutes(5) + Silence + TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, late.ConversationsConcluded);
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task Only_participants_holding_open_exchanges_are_polled()
    {
        // §5.0, and not an optimisation: an agent whose exchanges have all closed has already
        // said what it had to say, and under §14's lazy spawn, polling it would instantiate an
        // idle agent purely to ask whether it was finished.
        var asked = await Ask(Orders, Billing);
        await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model, """{"resolutions":[]}""", T0.AddMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);

        // Billing now holds nothing open, so the poll is orders alone and settles at once.
        var proposed = await Conclude(Orders, finished: true);

        Assert.True(proposed.Concluded);

        var billingInbox = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(billingInbox.ClosurePolls);
    }

    [Fact]
    public async Task Opening_an_exchange_voids_a_poll_and_discards_the_votes()
    {
        // §5.0: new activity discards any in-flight poll and every vote already cast, and
        // closure begins again from nothing. Discarding the votes is the part that matters — an
        // agent that said it was finished may not be, once a peer reopens the subject.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        Assert.Equal(ConversationState.Closing, await StateAsync());

        await Ask(Orders, Inventory, "which ledger owns stock?", at: T0.AddMinutes(6));

        Assert.Equal(ConversationState.Open, await StateAsync());

        // The state reset alone does not prove the votes went with it, and an earlier version of
        // this test asserted only that — mutation testing caught it, by removing the vote
        // discarding and watching this pass. The evidence is that orders, which had already
        // agreed, is asked again: an outstanding poll is one with no vote against it, so a
        // surviving 'done' would keep this empty.
        await Conclude(Inventory, finished: true, at: T0.AddMinutes(7));
        Assert.Equal(ConversationState.Closing, await StateAsync());

        var ordersInbox = await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(8), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Conversation, Assert.Single(ordersInbox.ClosurePolls).ConversationId);
    }

    [Fact]
    public async Task A_voided_poll_puts_the_question_back_to_everyone()
    {
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Ask(Orders, Inventory, "which ledger owns stock?", at: T0.AddMinutes(6));
        await Conclude(Billing, finished: true, at: T0.AddMinutes(7));

        // Orders was polled before the void and must be asked again, not assumed.
        var ordersInbox = await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(8), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Conversation, Assert.Single(ordersInbox.ClosurePolls).ConversationId);
    }

    [Fact]
    public async Task A_decline_lapses_when_its_holder_is_retired()
    {
        // §5.0: a judgement is only worth honouring while the agent that made it can still act
        // on it, and a discussion held open by a repository that no longer exists is the churn
        // §14 exists to stop.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));
        Assert.Equal(ConversationState.Closing, await StateAsync());

        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);

        var swept = await _broker.TickAsync(
            T0.AddMinutes(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, swept.ConversationsConcluded);
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task A_decline_lapses_when_its_holder_is_unreachable()
    {
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));

        await _agents.MarkUnreachableAsync(Billing, TestContext.Current.CancellationToken);

        var swept = await _broker.TickAsync(
            T0.AddMinutes(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, swept.ConversationsConcluded);
    }

    [Fact]
    public async Task A_restart_does_not_let_one_participant_close_a_discussion_alone()
    {
        // The worst thing found in this feature, and it needed no adversary — only a restart.
        // §14 orphans every open exchange at start-up, so counting only OPEN left the poll with
        // nobody to ask, and the proposer concluded a live discussion single-handed while its
        // peer still owed an answer. The replayed answer would then have arrived in a closed
        // conversation nobody may add to.
        await Ask(Orders, Billing);

        var second = new Broker(_connection, BrokerOptions.Default);
        await second.StartAsync(T0.AddMinutes(2), TestContext.Current.CancellationToken);

        var proposed = await second.ConcludeAsync(
            Conversation, Orders, true, T0.AddMinutes(3), TestContext.Current.CancellationToken);

        Assert.False(proposed.Concluded);
        Assert.Equal(ConversationState.Closing, await StateAsync());

        // And billing is genuinely asked, rather than merely not-yet-assented.
        var billingInbox = await second.DrainInboxAsync(
            Billing, T0.AddMinutes(4), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Conversation, Assert.Single(billingInbox.ClosurePolls).ConversationId);
    }

    [Fact]
    public async Task A_gate_crasher_cannot_veto_once_its_exchange_has_closed()
    {
        // §5.0 accepts that an identifier is a claim rather than a verified fact — an agent may
        // name a discussion it was not part of and thereby join it — and bounds the damage with
        // one sentence: "keeping the veto alive means keeping an exchange open, which the
        // windows in §6 will eventually close." Found by probe: an earlier version exempted the
        // proposer from the open-exchange rule, and a discussion stayed open for a week on a
        // veto from an agent whose only exchange had closed days before.
        var crasher = await Ask(Inventory, Billing, "who owns stock?", at: T0.AddMinutes(1));
        Assert.True(crasher.Succeeded);

        await _broker.AnswerAsync(
            crasher.Exchange!.Id, Billing, Model, """{"resolutions":[]}""", T0.AddMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);

        // The real discussion carries on between the two that are still working.
        await Ask(Orders, Billing, at: T0.AddMinutes(3));

        // The crasher objects first, which is what would have opened the poll in its own name.
        await Conclude(Inventory, finished: false, at: T0.AddMinutes(4));
        await Conclude(Orders, finished: true, at: T0.AddMinutes(5));
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task Declining_a_discussion_nobody_proposed_closing_starts_no_poll()
    {
        // Saying "not finished" about an open conversation states what open already means.
        // Opening a poll on it would put a closure question nobody proposed to every other
        // participant — and under §5.0 their silence would then be recorded as assent to it.
        // §5.0 ties proposing to an agent that "says it is finished"; this is the other half.
        await Ask(Orders, Billing);

        var declined = await Conclude(Billing, finished: false);

        Assert.True(declined.Succeeded);
        Assert.False(declined.Concluded);
        Assert.Equal(ConversationState.Open, await StateAsync());

        var ordersInbox = await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(ordersInbox.ClosurePolls);
    }

    [Fact]
    public async Task A_proposer_that_holds_nothing_open_still_opens_the_poll()
    {
        // The half that must keep working. Any participant may propose, and one that is not
        // polled has already said the only thing the poll would have asked it — so its proposal
        // still puts the question to everyone who is still working.
        var settled = await Ask(Inventory, Billing, "who owns stock?", at: T0.AddMinutes(1));
        await _broker.AnswerAsync(
            settled.Exchange!.Id, Billing, Model, """{"resolutions":[]}""", T0.AddMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);

        await Ask(Orders, Billing, at: T0.AddMinutes(3));

        await Conclude(Inventory, finished: true, at: T0.AddMinutes(4));

        Assert.Equal(ConversationState.Closing, await StateAsync());

        var billingInbox = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Conversation, Assert.Single(billingInbox.ClosurePolls).ConversationId);
    }

    [Fact]
    public async Task An_agent_outside_the_conversation_learns_nothing()
    {
        // The refusal deliberately cannot distinguish "not your discussion" from "no such
        // discussion". Telling them apart would let an agent probe which identifiers exist and
        // who is talking about what, which is the disclosure §5.0 spends most of its length
        // preventing.
        await Ask(Orders, Billing);

        var intruder = await Conclude(Inventory, finished: true);
        var invented = await _broker.ConcludeAsync(
            "no-such-discussion", Inventory, true, T0.AddMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(ClosureError.NotAParticipant, intruder.Error);
        Assert.Equal(ClosureError.NotAParticipant, invented.Error);
        Assert.Equal(ConversationState.Open, await StateAsync());
    }

    [Fact]
    public async Task Concluding_a_closed_conversation_says_so_and_changes_nothing()
    {
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        // Even a decline, which would otherwise block. Closure is terminal: §5.0 refuses to make
        // reopening a special case, because a discussion worth resuming is a new discussion.
        var late = await Conclude(Billing, finished: false, at: T0.AddMinutes(7));

        Assert.True(late.Concluded);
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task The_poll_reaches_the_inbox_and_leaves_once_answered()
    {
        // Surfaced through inbox() rather than as a message, because §5 admits four message
        // types and no others — and a fifth carrying broker questions would make the broker a
        // participant that agents address, which it is not.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);

        var polled = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(6), cancellationToken: TestContext.Current.CancellationToken);

        var poll = Assert.Single(polled.ClosurePolls);
        Assert.Equal(Conversation, poll.ConversationId);
        Assert.Equal(poll.PolledUtc + Silence, poll.ExpiresUtc);

        await Conclude(Billing, finished: false, at: T0.AddMinutes(7));

        var answered = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(8), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(answered.ClosurePolls);
    }

    [Fact]
    public async Task The_proposer_is_not_asked_a_question_it_has_already_answered()
    {
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);

        var ordersInbox = await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(6), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(ordersInbox.ClosurePolls);
    }

    [Fact]
    public async Task An_exchange_still_owed_survives_its_conversation_concluding()
    {
        // Reachable without anything unusual: nothing in §5.0 requires the exchanges in a
        // conversation to be closed before consensus closes it, so billing can still owe orders
        // an answer in a discussion that has concluded. Closure governs whether a new Question
        // may name the identifier (§7.1), not whether work already in flight may finish — and
        // severing it would discard an answer a peer had gone to the trouble of producing.
        var asked = await Ask(Orders, Billing);
        await Conclude(Orders, finished: true, at: T0.AddMinutes(2));
        Assert.True((await Conclude(Billing, finished: true, at: T0.AddMinutes(3))).Concluded);

        Assert.True((await _broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, """{"text":"gross of what?"}""",
            T0.AddMinutes(4), TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await _broker.ClarifyAsync(
            asked.Exchange.Id, Orders, Model, """{"text":"of VAT."}""",
            T0.AddMinutes(5), TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await _broker.AnswerAsync(
            asked.Exchange.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            T0.AddMinutes(6), cancellationToken: TestContext.Current.CancellationToken)).Succeeded);

        var inbox = await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(7), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, inbox.Messages.Count);
    }

    [Fact]
    public async Task An_exchange_standing_alone_joins_no_conversation()
    {
        // The common case, per §5.0: absent an identifier each exchange stands alone.
        Assert.True((await Ask(Orders, Billing, conversationId: null)).Succeeded);

        Assert.Null(await _conversations.GetAsync(
            Conversation, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Voiding_cannot_reopen_a_conversation_that_concluded()
    {
        // Closure is terminal. New activity is what voids a poll, and new activity is refused on
        // a closed conversation — but the void is a public repository operation, so the state
        // filter is what stops a second implementation or an operator surface from finding a
        // back door here.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        var voided = await _conversations.VoidPollAsync(
            Conversation, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(voided);
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task A_conversation_that_concluded_records_when()
    {
        // §5.0 exists so that "we concluded" and "nobody spoke for a while" stay distinguishable,
        // and §15's exchanges-per-promoted-decision is only meaningful if they do. A closure with
        // no instant against it would lose half of that.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        var record = await _conversations.GetAsync(
            Conversation, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(T0.AddMinutes(6), record!.ClosedUtc);
    }

    [Fact]
    public async Task The_store_refuses_a_vote_from_an_agent_that_was_never_polled()
    {
        // The broker refuses a non-participant before reaching here. This is the layer beneath,
        // which matters because the repository is public: an outsider's vote is refused by the
        // WHERE clause rather than by the caller having remembered to check, and no membership
        // row is conjured for it on the way.
        await Ask(Orders, Billing);
        await Conclude(Orders, finished: true);

        var recorded = await _conversations.RecordVoteAsync(
            Conversation, Inventory, Vote.NotDone, T0.AddMinutes(6),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(recorded);
        Assert.False(await _conversations.IsParticipantAsync(
            Conversation, Inventory, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_inbox_with_neither_messages_nor_polls_reports_itself_empty()
    {
        await Ask(Orders, Billing);

        var quiet = await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(quiet.IsEmpty);

        var busy = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(busy.IsEmpty);
    }

    [Fact]
    public async Task Concluding_before_start_is_refused()
    {
        var broker = new Broker(_connection, BrokerOptions.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ConcludeAsync(
            Conversation, Orders, true, T0, TestContext.Current.CancellationToken));
    }
}
