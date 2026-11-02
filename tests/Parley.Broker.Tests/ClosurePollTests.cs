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
/// A conversation ends by consensus or by the person, and nothing else ends one (principle 2.8). So "we are
/// finished" and "nobody spoke for a while" stay different facts: silence never assents, and a participant that
/// declines blocks until it changes its mind, is retired, or the person closes the conversation.
/// </para>
/// <para>
/// Much of what follows is about what the poll refuses to disclose. Each participant is asked whether it is
/// finished and told nothing about who else is in the conversation, how many there are, or how anyone answered.
/// </para>
/// </remarks>
[Integration]
public sealed class ClosurePollTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "pricing";

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ConversationRepository _conversations;
    private readonly Broker _broker;
    private readonly OperatorConsole _operator;

    public ClosurePollTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing, Inventory);

        _agents = new AgentRepository(_connection);
        _conversations = new ConversationRepository(_connection);
        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, Ct).GetAwaiter().GetResult();

        // §10.2 lives on its own type, so a broker handed to an adapter carries no operator operation.
        _operator = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<RequestOutcome> Send(
        AgentName from, AgentName to, string text = "gross or net?", string? conversationId = Conversation, DateTimeOffset? at = null) =>
        _broker.RequestAsync(from, to, Requests.Model, Requests.Request(text), at ?? T0.AddMinutes(1), conversationId, Ct);

    private Task<SayOutcome> Finish(RequestOutcome sent, AgentName receiver, int minute) =>
        _broker.ResultAsync(sent.Exchange!.Id, receiver, Requests.Model, Requests.Result(), T0.AddMinutes(minute), Ct);

    private Task<ClosureOutcome> Conclude(AgentName agent, bool finished, DateTimeOffset? at = null) =>
        _broker.ConcludeAsync(Conversation, agent, finished, at ?? T0.AddMinutes(5), Ct);

    private async Task<ConversationState> StateAsync() =>
        (await _conversations.GetAsync(Conversation, cancellationToken: Ct))!.State;

    // ---- membership ----

    [Fact]
    public async Task Being_party_to_an_exchange_is_what_makes_an_agent_a_participant()
    {
        Assert.True((await Send(Orders, Billing)).Succeeded);

        Assert.True(await _conversations.IsParticipantAsync(Conversation, Orders, cancellationToken: Ct));
        Assert.True(await _conversations.IsParticipantAsync(Conversation, Billing, cancellationToken: Ct));
        Assert.False(await _conversations.IsParticipantAsync(Conversation, Inventory, cancellationToken: Ct));
    }

    [Fact]
    public async Task Membership_grows_and_traffic_stays_pairwise()
    {
        Assert.True((await Send(Orders, Billing)).Succeeded);
        Assert.True((await Send(Orders, Inventory, "which ledger owns stock?")).Succeeded);

        foreach (var agent in new[] { Orders, Billing, Inventory })
        {
            Assert.True(await _conversations.IsParticipantAsync(Conversation, agent, cancellationToken: Ct));
        }

        // Billing's inbox holds its own Request and nothing of inventory's.
        Assert.Single((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct)).Messages);
    }

    [Fact]
    public async Task A_request_naming_nothing_starts_a_conversation_of_its_own()
    {
        var sent = await Send(Orders, Billing, conversationId: null);

        Assert.StartsWith("c-", sent.Request!.ConversationId, StringComparison.Ordinal);
        Assert.Null(await _conversations.GetAsync(Conversation, cancellationToken: Ct));
    }

    // ---- the poll ----

    [Fact]
    public async Task A_proposal_leaves_the_conversation_open_until_the_others_answer()
    {
        await Send(Orders, Billing);

        var proposed = await Conclude(Orders, finished: true);

        Assert.True(proposed.Succeeded);
        Assert.False(proposed.Concluded);
        Assert.Equal(ConversationState.Closing, await StateAsync());
    }

    [Fact]
    public async Task Closure_is_reached_when_every_polled_participant_agrees()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);

        Assert.True((await Conclude(Billing, finished: true, at: T0.AddMinutes(6))).Concluded);
        Assert.Equal("Consensus", (await _conversations.GetAsync(Conversation, cancellationToken: Ct))!.ClosedBy.ToString());
    }

    [Fact]
    public async Task Work_still_open_when_everyone_agrees_ends_closed_and_both_parties_are_told()
    {
        // Agreeing a conversation is finished is agreeing the work in it is too. Neither party is left holding an
        // exchange that nothing will ever revisit.
        var sent = await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.Equal(ExchangeOutcome.Closed, (await new ExchangeRepository(_connection).GetAsync(sent.Exchange!.Id, cancellationToken: Ct))!.Outcome);

        var notice = Assert.Single((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(7), cancellationToken: Ct)).Messages);
        Assert.Equal(Responder.Broker, notice.Message.ResponderModel);
    }

    [Fact]
    public async Task A_decline_blocks_and_is_not_outvoted()
    {
        // A majority that can conclude over an objection is authority over another agent by a different name.
        await Send(Orders, Billing);
        await Send(Orders, Inventory, "which ledger owns stock?");

        await Conclude(Orders, finished: true);
        await Conclude(Inventory, finished: true, at: T0.AddMinutes(6));

        Assert.False((await Conclude(Billing, finished: false, at: T0.AddMinutes(7))).Concluded);

        await _broker.TickAsync(T0.AddDays(1), Ct);
        Assert.Equal(ConversationState.Closing, await StateAsync());
    }

    [Fact]
    public async Task A_participant_that_declined_may_change_its_mind()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));

        Assert.True((await Conclude(Billing, finished: true, at: T0.AddMinutes(7))).Concluded);
    }

    [Fact]
    public async Task Silence_never_assents()
    {
        // An agent busy doing the work has not agreed the work is over, and
        // closing over it would end the conversation it is working in. What ends a stalled poll is the person.
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);

        Assert.Equal(0, (await _broker.TickAsync(T0.AddYears(1), Ct)).ConversationsConcluded);
        Assert.Equal(ConversationState.Closing, await StateAsync());
    }

    [Fact]
    public async Task Only_participants_holding_open_exchanges_are_polled()
    {
        // Not an optimisation: a participant whose exchanges have all closed has said what it had to say, and
        // polling it would start an idle agent purely to ask whether it was finished.
        var sent = await Send(Orders, Billing);
        await Finish(sent, Billing, 2);

        Assert.True((await Conclude(Orders, finished: true)).Concluded);
        Assert.Empty((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(6), cancellationToken: Ct)).ClosurePolls);
    }

    [Fact]
    public async Task Sending_a_request_voids_a_poll_and_discards_the_votes()
    {
        // A participant that said it was finished may not be, once somebody reopens the subject.
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        Assert.Equal(ConversationState.Closing, await StateAsync());

        await Send(Orders, Inventory, "which ledger owns stock?", at: T0.AddMinutes(6));
        Assert.Equal(ConversationState.Open, await StateAsync());

        // The evidence the votes went: orders, which had agreed, is asked again.
        await Conclude(Inventory, finished: true, at: T0.AddMinutes(7));
        Assert.Equal(ConversationState.Closing, await StateAsync());

        Assert.Equal(Conversation, Assert.Single((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(8), cancellationToken: Ct)).ClosurePolls).ConversationId);
    }

    [Fact]
    public async Task A_decline_lapses_when_its_holder_is_retired()
    {
        // A judgement is only worth honouring while the agent that made it can still act on it.
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));

        await _agents.RetireAsync(Billing, Ct);

        Assert.Equal(1, (await _broker.TickAsync(T0.AddMinutes(10), Ct)).ConversationsConcluded);
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task A_decline_does_not_lapse_merely_because_its_holder_is_silent()
    {
        // A decline that lapsed on silence would close conversations under agents that were only closed at the
        // time, which is every agent overnight.
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));

        GoSilent(Billing);

        Assert.Equal(0, (await _broker.TickAsync(T0.AddMinutes(10), Ct)).ConversationsConcluded);
        Assert.Equal(ConversationState.Closing, await StateAsync());
    }

    [Fact]
    public async Task A_restart_does_not_let_one_participant_close_a_conversation_alone()
    {
        // A restart ends nothing, so billing still holds the work and is still asked.
        await Send(Orders, Billing);

        var second = new Broker(_connection, BrokerOptions.Default);
        await second.StartAsync(Orders, T0.AddMinutes(2), Ct);

        Assert.False((await second.ConcludeAsync(Conversation, Orders, true, T0.AddMinutes(3), Ct)).Concluded);
        Assert.Equal(Conversation, Assert.Single((await second.DrainInboxAsync(Billing, T0.AddMinutes(4), cancellationToken: Ct)).ClosurePolls).ConversationId);
    }

    [Fact]
    public async Task A_participant_whose_work_is_finished_cannot_veto()
    {
        // Billing, working on orders' Request, asks inventory for part of it, and inventory finishes. Inventory
        // is a participant, holds nothing, and so is not asked — its objection keeps nothing open.
        var fromOrders = await Send(Orders, Billing);
        Assert.True(fromOrders.Succeeded);

        var onward = await Send(Billing, Inventory, "how much stock?", conversationId: null, at: T0.AddMinutes(2));
        Assert.Equal(Conversation, onward.Request!.ConversationId);
        await Finish(onward, Inventory, 3);

        await Conclude(Inventory, finished: false, at: T0.AddMinutes(4));
        await Conclude(Orders, finished: true, at: T0.AddMinutes(5));
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task Declining_a_conversation_nobody_proposed_closing_starts_no_poll()
    {
        // Saying "not finished" about an open conversation states what open already means.
        await Send(Orders, Billing);

        var declined = await Conclude(Billing, finished: false);

        Assert.True(declined.Succeeded);
        Assert.False(declined.Concluded);
        Assert.Equal(ConversationState.Open, await StateAsync());
        Assert.Empty((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(6), cancellationToken: Ct)).ClosurePolls);
    }

    [Fact]
    public async Task A_proposer_that_holds_nothing_open_still_opens_the_poll()
    {
        await Send(Orders, Billing);
        var onward = await Send(Billing, Inventory, "how much stock?", conversationId: null, at: T0.AddMinutes(2));
        await Finish(onward, Inventory, 3);

        await Conclude(Inventory, finished: true, at: T0.AddMinutes(4));

        Assert.Equal(ConversationState.Closing, await StateAsync());
        Assert.Single((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(5), cancellationToken: Ct)).ClosurePolls);
    }

    [Fact]
    public async Task An_agent_outside_the_conversation_learns_nothing()
    {
        // The refusal cannot distinguish "not your conversation" from "no such conversation", so identifiers
        // cannot be probed for.
        await Send(Orders, Billing);

        var intruder = await Conclude(Inventory, finished: true);
        var invented = await _broker.ConcludeAsync("no-such-conversation", Inventory, true, T0.AddMinutes(5), Ct);

        Assert.Equal(ClosureError.NotAParticipant, intruder.Error);
        Assert.Equal(ClosureError.NotAParticipant, invented.Error);
        Assert.Equal(ConversationState.Open, await StateAsync());
    }

    [Fact]
    public async Task Concluding_a_closed_conversation_says_so_and_changes_nothing()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.True((await Conclude(Billing, finished: false, at: T0.AddMinutes(7))).Concluded);
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task The_poll_reaches_the_inbox_and_leaves_once_answered()
    {
        // Surfaced through inbox() rather than as a message: §5 admits three message types and no others.
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);

        Assert.Equal(Conversation, Assert.Single((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(6), cancellationToken: Ct)).ClosurePolls).ConversationId);

        await Conclude(Billing, finished: false, at: T0.AddMinutes(7));

        Assert.Empty((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(8), cancellationToken: Ct)).ClosurePolls);
    }

    [Fact]
    public async Task The_proposer_is_not_asked_a_question_it_has_already_answered()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);

        Assert.Empty((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(6), cancellationToken: Ct)).ClosurePolls);
    }

    [Fact]
    public async Task A_vote_against_a_concluded_conversation_is_not_recorded()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.False(await _conversations.RecordVoteAsync(Conversation, Billing, Vote.NotDone, T0.AddMinutes(7), cancellationToken: Ct));
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task A_reinstated_agent_gets_its_judgement_back()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));

        await _agents.RetireAsync(Billing, Ct);
        await _agents.ReinstateAsync(Billing, Ct);

        Assert.Equal(0, (await _broker.TickAsync(T0.AddDays(1), Ct)).ConversationsConcluded);
        Assert.Equal(ConversationState.Closing, await StateAsync());
    }

    [Fact]
    public async Task A_poll_in_one_conversation_does_not_touch_another()
    {
        await Send(Orders, Billing);
        await Send(Orders, Inventory, "which ledger owns stock?", "shipping", T0.AddMinutes(2));

        await Conclude(Orders, finished: true, at: T0.AddMinutes(3));

        Assert.Equal(ConversationState.Open, (await _conversations.GetAsync("shipping", cancellationToken: Ct))!.State);
        Assert.Empty((await _broker.DrainInboxAsync(Inventory, T0.AddMinutes(4), cancellationToken: Ct)).ClosurePolls);
    }

    [Fact]
    public async Task Voiding_cannot_reopen_a_conversation_that_concluded()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.False(await _conversations.VoidPollAsync(Conversation, cancellationToken: Ct));
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task A_conversation_that_concluded_records_when()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.Equal(T0.AddMinutes(6), (await _conversations.GetAsync(Conversation, cancellationToken: Ct))!.ClosedUtc);
    }

    [Fact]
    public async Task The_store_refuses_a_vote_from_an_agent_that_was_never_polled()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);

        Assert.False(await _conversations.RecordVoteAsync(Conversation, Inventory, Vote.NotDone, T0.AddMinutes(6), cancellationToken: Ct));
        Assert.False(await _conversations.IsParticipantAsync(Conversation, Inventory, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_inbox_with_neither_messages_nor_polls_reports_itself_empty()
    {
        await Send(Orders, Billing);

        Assert.True((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(2), cancellationToken: Ct)).IsEmpty);
        Assert.False((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct)).IsEmpty);
    }

    // ---- the person's close ----

    [Fact]
    public async Task The_person_can_end_a_conversation_a_decline_has_blocked()
    {
        // Without this the poll introduces a state nothing can resolve: the tick will not conclude over an
        // objection, and a decline only lapses if its holder is retired.
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: false, at: T0.AddMinutes(6));

        await _broker.TickAsync(T0.AddDays(1), Ct);
        Assert.Equal(ConversationState.Closing, await StateAsync());

        Assert.True(await _operator.ForceConcludeAsync(Conversation, "billing's objection is resolved elsewhere", T0.AddDays(1), Ct));
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task The_person_can_end_a_conversation_nobody_proposed_closing()
    {
        await Send(Orders, Billing);

        Assert.True(await _operator.ForceConcludeAsync(Conversation, null, T0.AddMinutes(5), Ct));
        Assert.Equal(ConversationState.Closed, await StateAsync());
    }

    [Fact]
    public async Task A_forced_close_takes_an_outstanding_poll_out_of_the_inbox()
    {
        // A participant left holding a poll for a closed conversation would answer a question that no longer
        // exists, and its vote is refused, so it would never stop being asked.
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);

        Assert.Single((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(6), cancellationToken: Ct)).ClosurePolls);

        await _operator.ForceConcludeAsync(Conversation, null, T0.AddMinutes(7), Ct);

        Assert.Empty((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(8), cancellationToken: Ct)).ClosurePolls);
    }

    [Fact]
    public async Task Forcing_a_conversation_that_already_concluded_changes_nothing()
    {
        await Send(Orders, Billing);
        await Conclude(Orders, finished: true);
        await Conclude(Billing, finished: true, at: T0.AddMinutes(6));

        Assert.False(await _operator.ForceConcludeAsync(Conversation, null, T0.AddDays(1), Ct));

        // The original instant and closer survive, so the log still says the participants agreed.
        var record = await _conversations.GetAsync(Conversation, cancellationToken: Ct);
        Assert.Equal(T0.AddMinutes(6), record!.ClosedUtc);
        Assert.Equal(ConversationCloser.Consensus, record.ClosedBy);
    }

    [Fact]
    public async Task A_forced_close_refuses_further_requests_and_says_who_closed_it()
    {
        await Send(Orders, Billing);
        await _operator.ForceConcludeAsync(Conversation, null, T0.AddMinutes(5), Ct);

        var refused = await Send(Orders, Inventory, "which ledger owns stock?", at: T0.AddMinutes(6));

        Assert.Equal(RequestRejection.ConversationClosed, refused.Rejection);
        Assert.Equal(ConversationCloser.Person, refused.Admission.ClosedBy);
    }

    [Fact]
    public async Task Concluding_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Broker(_connection).ConcludeAsync(Conversation, Orders, true, T0, Ct));
    }

    /// <summary>Takes an agent's machine off the air, the way a lost node does.</summary>
    private void GoSilent(AgentName agent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE agents SET connected = 0 WHERE name = @Name;
            UPDATE nodes  SET connected = 0
             WHERE machine = (SELECT machine FROM agents WHERE name = @Name);
            """;
        command.Parameters.AddWithValue("@Name", agent.Value);
        command.ExecuteNonQuery();
    }
}
