using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Collecting an inbox, which is where the two stages of SPEC.md §6 join up.
/// </summary>
/// <remarks>
/// Collection is the observation that separates two facts about silence. Before it, nobody picked the work
/// up; after it, somebody has it. §6 flags the two on different thresholds, and this is the only place the
/// transition happens.
/// </remarks>
[Integration]
public sealed class InboxTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly Broker _broker;

    public InboxTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing);

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);

        _broker = new Broker(_connection);
        _broker.StartAsync(Orders, T0.AddHours(-1), Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RequestOutcome> Send(AgentName from, AgentName to, string text = "gross or net?", string? conversation = null)
    {
        var sent = await _broker.RequestAsync(from, to, Requests.Model, Requests.Request(text), T0, conversation, Ct);
        Assert.True(sent.Succeeded, $"expected to send {from} -> {to}, got {sent.Rejection}");
        return sent;
    }

    private Task<Inbox> Drain(AgentName agent, DateTimeOffset at, int? limit = null, Broker? broker = null) =>
        (broker ?? _broker).DrainInboxAsync(agent, at, limit, Ct);

    [Fact]
    public async Task Draining_returns_what_was_waiting_and_marks_it_collected()
    {
        await Send(Orders, Billing);

        Assert.Single((await Drain(Billing, T0.AddMinutes(1))).Messages);
        Assert.Empty((await Drain(Billing, T0.AddMinutes(2))).Messages);
    }

    [Fact]
    public async Task Collecting_a_request_records_it_on_the_exchange_once()
    {
        // Where §6's second stage begins. Only the first collection counts: a Request given again after a
        // restart was collected before, and restamping it would hide how long it has been owed.
        var sent = await Send(Orders, Billing);

        await Drain(Billing, T0.AddMinutes(1));

        Assert.Equal(T0.AddMinutes(1), (await _exchanges.GetAsync(sent.Exchange!.Id, cancellationToken: Ct))!.DeliveredUtc);

        await _messages.RedeliverAsync(sent.Request!.Id, Ct);
        await Drain(Billing, T0.AddHours(5));

        Assert.Equal(T0.AddMinutes(1), (await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct))!.DeliveredUtc);
    }

    [Fact]
    public async Task Debts_are_returned_before_anything_else()
    {
        // SPEC.md §7.3. Billing is owed a Result and separately owes a Request. The debt comes first however the
        // rows happen to be ordered, because the work owed frequently reshapes what the agent was about to do.
        var billingAsked = await Send(Billing, Orders, "which service owns tax?", "tax");
        await _broker.ResultAsync(billingAsked.Exchange!.Id, Orders, Requests.Model, Requests.Result(), T0.AddSeconds(1), Ct);

        await Send(Orders, Billing, "gross or net?", "pricing");

        var drained = (await Drain(Billing, T0.AddMinutes(1))).Messages;

        Assert.Equal([MessageType.Request, MessageType.Result], drained.Select(d => d.Message.Type));
    }

    [Fact]
    public async Task A_result_arrives_with_the_request_it_closes()
    {
        // The requester's context will have moved on, and an identifier alone means nothing to a model that no
        // longer remembers sending it (§4.2, §13).
        var sent = await Send(Orders, Billing, "gross or net?", "pricing");
        await _broker.ResultAsync(sent.Exchange!.Id, Billing, Requests.Model, Requests.Result(), T0.AddMinutes(1), Ct);

        var delivered = Assert.Single((await Drain(Orders, T0.AddMinutes(2))).Messages);

        Assert.Contains("--- begin your request", delivered.Envelope, StringComparison.Ordinal);
        Assert.Contains("gross or net?", delivered.Envelope, StringComparison.Ordinal);
        Assert.Contains("in conversation `pricing`", delivered.Envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_arrives_without_a_request_block()
    {
        await Send(Orders, Billing);

        var delivered = Assert.Single((await Drain(Billing, T0.AddMinutes(1))).Messages);

        Assert.DoesNotContain("your request", delivered.Envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draining_respects_a_limit()
    {
        await Send(Orders, Billing, "one");
        await Send(Orders, Billing, "two");
        await Send(Orders, Billing, "three");

        Assert.Equal(2, (await Drain(Billing, T0.AddMinutes(1), limit: 2)).Messages.Count);
    }

    [Fact]
    public async Task Work_collected_by_a_session_that_ended_is_handed_over_again()
    {
        // §6: an agent coming back is given what it had collected and not finished. Nothing about the exchange
        // changes — it was open throughout.
        var sent = await Send(Orders, Billing);

        var billing = new Broker(_connection);
        await billing.StartAsync(Billing, T0, Ct);
        await Drain(Billing, T0.AddMinutes(1), broker: billing);
        await billing.DetachAsync(Ct);

        var restarted = new Broker(_connection);
        var start = await restarted.StartAsync(Billing, T0.AddHours(2), Ct);

        Assert.Equal(1, start.ExchangesRedelivered);
        Assert.Equal(sent.Request!.Id, Assert.Single((await Drain(Billing, T0.AddHours(2), broker: restarted)).Messages).Message.Id);
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(sent.Exchange!.Id, cancellationToken: Ct))!.State);
    }

    [Fact]
    public async Task A_second_window_beside_a_live_session_is_given_nothing_again()
    {
        // Two windows on one repository are two sessions for one agent, and the second must not re-deliver what
        // the first is working on.
        await Send(Orders, Billing);

        var first = new Broker(_connection);
        await first.StartAsync(Billing, T0, Ct);
        await Drain(Billing, T0.AddMinutes(1), broker: first);

        var second = new Broker(_connection);

        Assert.Equal(0, (await second.StartAsync(Billing, T0.AddMinutes(2), Ct)).ExchangesRedelivered);
        Assert.Empty((await Drain(Billing, T0.AddMinutes(3), broker: second)).Messages);
    }

    [Fact]
    public async Task What_is_handed_over_again_is_the_latest_message_owed_not_the_first()
    {
        // An exchange that got as far as a clarification owes a reply to the clarification.
        var sent = await Send(Orders, Billing);
        await Drain(Billing, T0.AddMinutes(1));
        await _broker.ClarifyAsync(sent.Exchange!.Id, Billing, Requests.Model, Requests.Clarify("gross of what tax?"), T0.AddMinutes(2), Ct);

        await Drain(Orders, T0.AddMinutes(3));
        await _broker.DetachAsync(Ct);

        var restarted = new Broker(_connection);
        await restarted.StartAsync(Orders, T0.AddHours(2), Ct);

        Assert.Equal(MessageType.Clarify, Assert.Single((await Drain(Orders, T0.AddHours(2), broker: restarted)).Messages).Message.Type);
    }

    [Fact]
    public async Task An_agent_is_never_handed_its_own_message()
    {
        // An exchange holds traffic in both directions; handing back the wrong half would put an agent's own
        // Request into its own inbox, which reads as something to do.
        await Send(Orders, Billing);
        await Drain(Billing, T0.AddMinutes(1));
        await _broker.DetachAsync(Ct);

        var restarted = new Broker(_connection);
        await restarted.StartAsync(Orders, T0.AddHours(2), Ct);

        Assert.Empty((await Drain(Orders, T0.AddHours(2), broker: restarted)).Messages);
    }

    [Fact]
    public async Task An_already_collected_message_is_not_handed_over_again()
    {
        var sent = await Send(Orders, Billing);

        Assert.True(await _messages.MarkDeliveredAsync(sent.Request!.Id, T0.AddSeconds(30), Ct));

        Assert.Empty((await Drain(Billing, T0.AddMinutes(1))).Messages);
    }

    [Fact]
    public async Task A_request_whose_conversation_was_closed_before_it_was_collected_is_not_handed_over()
    {
        // It asks for a move and the exchange has none left, so an agent given it would spend a turn being
        // refused. What it is handed instead is the broker's word that the work was closed.
        await Send(Orders, Billing, "gross or net?", "pricing");
        await new ConversationRepository(_connection).ForceConcludeAsync("pricing", "superseded", T0.AddMinutes(1), cancellationToken: Ct);

        var drained = (await Drain(Billing, T0.AddMinutes(2))).Messages;

        var notice = Assert.Single(drained);
        Assert.Equal(MessageType.Result, notice.Message.Type);
        Assert.Equal(Responder.Broker, notice.Message.ResponderModel);
    }

    [Fact]
    public async Task A_closure_poll_is_surfaced_to_the_participants_it_asks()
    {
        // Billing still holds the Request, which is what makes it somebody worth asking (§5.0).
        await Send(Orders, Billing, "gross or net?", "pricing");

        Assert.False((await _broker.ConcludeAsync("pricing", Orders, finished: true, T0.AddMinutes(2), Ct)).Concluded);

        var poll = Assert.Single((await Drain(Billing, T0.AddMinutes(3))).ClosurePolls);
        Assert.Equal("pricing", poll.ConversationId);
    }

    [Fact]
    public async Task Draining_an_empty_inbox_is_not_an_error()
    {
        Assert.True((await Drain(Billing, T0)).IsEmpty);
    }

    [Fact]
    public async Task Draining_returns_only_what_is_addressed_to_the_caller()
    {
        await Send(Orders, Billing);

        Assert.Empty((await Drain(Orders, T0.AddMinutes(1))).Messages);
    }
}
