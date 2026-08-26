using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Draining an inbox, which is where the two windows in SPEC.md §6 join up.
/// </summary>
/// <remarks>
/// Delivery is the observation that splits one deadline into two. Before it, silence means
/// nobody collected the question; after it, silence means somebody has it and is working. That
/// distinction is the whole reason §6 could describe two windows without inventing an
/// acknowledgement message, and this is the only place the transition happens.
/// </remarks>
public sealed class InboxTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "\"consequences\":\"If yes I drop the column; if no I add a snapshot field.\"";

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly Broker _broker;

    public InboxTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Offline),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);

        // Started before anything is opened, as a deployment does. The reboot sequence orphans
        // every open exchange, so a test that opens first and starts second is testing a drain
        // over orphans rather than over live traffic — which is a different scenario, and the
        // one Draining_replays_what_the_agent_owes_before_reading covers deliberately.
        _broker = new Broker(_connection);
        _broker.StartAsync(Orders, T0.AddHours(-1), TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Exchange> Ask(AgentName asker, AgentName answerer, string question)
    {
        var outcome = await _exchanges.TryOpenAsync(
            asker, answerer, T0, question, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, $"expected to open {asker} -> {answerer}, got {outcome.Rejection}");
        return outcome.Exchange!;
    }

    private async Task<Message> Send(
        MessageType type, AgentName from, AgentName to, long? exchangeId, string text)
    {
        var payload = type == MessageType.Question
            ? $"{{\"text\":\"{text}\",{Consequences}}}"
            : $"{{\"text\":\"{text}\"}}";

        var outcome = await _messages.AppendAsync(
            exchangeId, type, from, to, "claude-opus-5", payload, T0,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Stored, "expected the message to be stored");
        return outcome.Message;
    }

    [Fact]
    public async Task Draining_returns_what_was_waiting()
    {
        var exchange = await Ask(Orders, Billing, "where does price live");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        var drained = (await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Single(drained);
    }

    [Fact]
    public async Task Draining_marks_what_it_returned_delivered()
    {
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        var broker = _broker;
        await broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        var again = (await broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Empty(again);
    }

    [Fact]
    public async Task Delivery_starts_the_working_window()
    {
        // The observation that splits the deadline. Before this the exchange is waiting to be
        // picked up and has minutes; afterwards it is being worked on and has hours.
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        Assert.Equal(T0 + ExchangePolicy.Default.DeliveryWindow, exchange.ExpiresUtc);

        var deliveredAt = T0.AddMinutes(1);
        await _broker.DrainInboxAsync(
            Billing, deliveredAt, cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await _exchanges.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(deliveredAt, loaded!.DeliveredUtc);
        Assert.Equal(deliveredAt + ExchangePolicy.Default.WorkingWindow, loaded.ExpiresUtc);
    }

    [Fact]
    public async Task An_undelivered_question_is_orphaned_on_the_short_window()
    {
        // The case the split exists for, from the other side: nobody collected it, so the agent
        // never spawned or died on the way, and waiting hours would help nobody.
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        var result = (await _broker.TickAsync(
            T0 + ExchangePolicy.Default.DeliveryWindow, TestContext.Current.CancellationToken)).Exchanges;

        Assert.Equal(1, result.Orphaned);
    }

    [Fact]
    public async Task A_delivered_question_survives_past_the_short_window()
    {
        // The same instant, the other outcome. An agent that collected the question and is
        // building something must not be orphaned out from under itself.
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        var broker = _broker;
        await broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        var result = (await broker.TickAsync(
            T0 + ExchangePolicy.Default.DeliveryWindow, TestContext.Current.CancellationToken)).Exchanges;

        Assert.Equal(0, result.Orphaned);

        var loaded = await _exchanges.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Open, loaded!.State);
    }

    [Fact]
    public async Task Debts_are_returned_before_anything_else()
    {
        // SPEC.md §7.3. Billing owes orders an answer, and separately is receiving one. The debt
        // comes first however the rows happen to be ordered by age, because the answer to a
        // question already owed frequently reshapes what the agent was about to do.
        // Billing asked orders something first, and orders has now answered it. The answer is
        // waiting in billing's inbox.
        //
        // This order matters: while billing -> orders is open, orders -> billing would close a
        // two-party cycle and be refused, which is cycle detection working rather than a quirk
        // of the test. Closing the first releases the edge.
        var asked = await Ask(Billing, Orders, "the question billing asked");
        await Send(MessageType.Question, Billing, Orders, asked.Id, "which service owns tax?");
        Assert.True(asked.Answer(Orders, T0.AddSeconds(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            asked, cancellationToken: TestContext.Current.CancellationToken));
        await Send(MessageType.Answer, Orders, Billing, asked.Id, "the invoice service does");

        // Then orders asks billing something, which billing now owes. It arrives later than the
        // answer above and must still be handed over first.
        var owed = await Ask(Orders, Billing, "the question billing owes");
        await Send(MessageType.Question, Orders, Billing, owed.Id, "gross or net?");

        var drained = (await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Equal(2, drained.Count);
        Assert.Equal(MessageType.Question, drained[0].Message.Type);
        Assert.Equal(MessageType.Answer, drained[1].Message.Type);
    }

    [Fact]
    public async Task A_notify_is_delivered_without_touching_any_deadline()
    {
        // The one type where delivery says nothing about anybody's window, because nothing is
        // owed in return.
        await Send(MessageType.Notify, Orders, Billing, null, "the contract gained a field");

        var drained = (await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Equal(MessageType.Notify, Assert.Single(drained).Message.Type);
    }

    [Fact]
    public async Task Draining_respects_a_limit()
    {
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");
        await Send(MessageType.Notify, Orders, Billing, null, "one");
        await Send(MessageType.Notify, Orders, Billing, null, "two");

        var drained = (await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), limit: 2,
            cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Equal(2, drained.Count);
    }

    [Fact]
    public async Task Draining_replays_what_the_agent_owes_before_reading()
    {
        // The inbound traffic §14 describes. Nothing is replayed at start-up; an agent
        // collecting its messages is what brings back what it owes, so a deployment does not pay
        // to instantiate agents that would have stayed idle.
        var exchange = await Ask(Orders, Billing, "q");
        var question = await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        // Billing collected the question during its last session and answered nothing. That is
        // what makes this a debt it was carrying rather than one it has yet to be handed, and
        // §14 releases only the first — see ExchangeRepository.GetAbandonedOwedByAsync.
        Assert.True(await _messages.MarkDeliveredAsync(
            question.Id, T0.AddSeconds(1), TestContext.Current.CancellationToken));

        var broker = new Broker(_connection, BrokerOptions.Default with
        {
            ResumePolicy = ResumePolicy.Always
        });
        await broker.StartAsync(Billing, T0.AddMinutes(1), TestContext.Current.CancellationToken);

        // The attach sequence orphaned it, because billing is what owes it.
        var afterStart = await _exchanges.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Orphaned, afterStart!.State);

        await broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);

        var afterDrain = await _exchanges.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Open, afterDrain!.State);
    }

    [Fact]
    public async Task An_already_delivered_message_is_not_handed_over_again()
    {
        // A caller acting on the same question twice is the failure the delivered stamp exists
        // to prevent.
        //
        // Worth being exact about which mechanism this covers. It is the pending query, which
        // filters on delivered_utc being null, so the message never enters the loop at all. The
        // loop also guards against MarkDeliveredAsync returning false, and that guard is not
        // exercised here or anywhere: reaching it needs a second writer to stamp the row between
        // the read and the write, which requires two connections racing — something the broker's
        // threading contract forbids and a single-connection test cannot produce. It is kept as
        // defence against a deployment that later relaxes that contract, in the same spirit as
        // the contention counter in Reaper.SweepAsync.
        var exchange = await Ask(Orders, Billing, "q");
        var message = await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        Assert.True(await _messages.MarkDeliveredAsync(
            message.Id, T0.AddSeconds(30), TestContext.Current.CancellationToken));

        var drained = (await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Empty(drained);
    }

    [Fact]
    public async Task Delivering_against_a_closed_exchange_starts_no_working_window()
    {
        // Two claims that used to be tested as one, and only the second survives.
        //
        // A closed exchange has no working window to start, so a delivery that lands on one
        // leaves its clock alone rather than failing. That still holds, and it is what makes the
        // race between the pending read and the delivery write harmless.
        //
        // What no longer holds is that the message is handed over at all — for a Question. It
        // asks for a move, and a closed exchange has none left, so an agent given one spends a
        // turn being refused. That was reachable in the worst way: an exchange ghosted while its
        // question sat uncollected handed the question to the peer at the same moment it told
        // the asker nobody had answered.
        //
        // A Clarify is still delivered, which is why this checks one: it is what a peer said
        // inside an exchange the recipient is party to, not a request nobody can act on.
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");
        await Send(MessageType.Clarify, Orders, Billing, exchange.Id, "gross of VAT.");

        Assert.True(exchange.Answer(Billing, T0.AddSeconds(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var drained = (await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Equal(MessageType.Clarify, Assert.Single(drained).Message.Type);

        var loaded = await _exchanges.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Closed, loaded!.State);
        Assert.Null(loaded.DeliveredUtc);
    }

    [Fact]
    public async Task A_question_whose_exchange_has_ended_is_not_handed_over()
    {
        // The case that made this worth changing. A question nobody collected before its
        // exchange ran out of time is ghosted, and §14 tells the asker that no answer is
        // coming — while the peer, drainining afterwards, was handed the question and refused
        // when it tried to answer. The two halves of one exchange disagreed.
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        Assert.True(exchange.Orphan(T0.AddMinutes(20)).Succeeded);
        Assert.True(exchange.Ghost(T0.AddDays(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var drained = (await _broker.DrainInboxAsync(
            Billing, T0.AddDays(2), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Empty(drained);
    }

    [Fact]
    public async Task A_replayed_question_is_handed_over_again()
    {
        // SPEC.md §14: replay is re-delivery, not re-sending. Without it the exchange reopens
        // and the agent has an obligation it cannot see — the question message is still stamped
        // delivered from before the restart, so the inbox is empty while the broker waits for an
        // answer nobody was asked for.
        var exchange = await Ask(Orders, Billing, "where does price live");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        var resuming = new Broker(_connection, BrokerOptions.Default with
        {
            ResumePolicy = ResumePolicy.Always
        });

        // Delivered, then interrupted, which is the ordinary case: the agent had the question
        // and the process went away before it answered. Billing is who attaches, because §14
        // now orphans what the attaching agent owes — and this debt is billing's.
        await resuming.StartAsync(Billing, T0.AddMinutes(-1), TestContext.Current.CancellationToken);
        await resuming.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        // The previous session stopped cleanly, which is what marks the agent offline and so
        // what tells the next attach that no worker is still holding these. Without it the
        // agent is still live and §14 declines to release anything — the right answer for a
        // second window opened alongside a live session, and the wrong one here.
        await resuming.DetachAsync(TestContext.Current.CancellationToken);

        // Well past the liveness lease, because that is what a restart looks like from the
        // registry: the previous session stopped renewing, and the lease lapsing is what
        // says so. Within the lease §14 declines to release anything, which is the right
        // answer for a second window opened beside a live session and the wrong one here.
        await resuming.StartAsync(Billing, T0.AddHours(2), TestContext.Current.CancellationToken);

        var afterRestart = (await resuming.DrainInboxAsync(
            Billing, T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Single(afterRestart);
        Assert.Equal(MessageType.Question, afterRestart[0].Message.Type);
    }

    [Fact]
    public async Task A_replay_hands_back_the_latest_message_not_the_first()
    {
        // An exchange that got as far as a clarification owes an answer to the clarification,
        // not to the original question. Replaying the whole exchange would hand back messages
        // the agent has already dealt with, and one of them it sent itself.
        var exchange = await Ask(Orders, Billing, "where does price live");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        // Billing clarifies, so the debt flips to orders, who now owes the answer.
        Assert.True(exchange.Clarify(Billing, T0.AddSeconds(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));
        await Send(MessageType.Clarify, Billing, Orders, exchange.Id, "gross of what tax?");

        var resuming = new Broker(_connection, BrokerOptions.Default with
        {
            ResumePolicy = ResumePolicy.Always
        });
        await resuming.StartAsync(Orders, T0.AddMinutes(-1), TestContext.Current.CancellationToken);
        await resuming.DrainInboxAsync(
            Orders, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        await resuming.DetachAsync(TestContext.Current.CancellationToken);

        await resuming.StartAsync(Orders, T0.AddHours(2), TestContext.Current.CancellationToken);

        var afterRestart = (await resuming.DrainInboxAsync(
            Orders, T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Equal(MessageType.Clarify, Assert.Single(afterRestart).Message.Type);
    }

    [Fact]
    public async Task A_replay_never_hands_an_agent_its_own_message()
    {
        // The filter that makes the replay safe rather than merely correct. An exchange holds
        // traffic in both directions, and re-delivering the wrong half would put an agent's own
        // question into its own inbox — which reads as something to answer.
        var exchange = await Ask(Orders, Billing, "where does price live");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        var resuming = new Broker(_connection, BrokerOptions.Default with
        {
            ResumePolicy = ResumePolicy.Always
        });
        await resuming.StartAsync(Orders, T0.AddMinutes(-1), TestContext.Current.CancellationToken);
        await resuming.DrainInboxAsync(
            Billing, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
        await resuming.StartAsync(Orders, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        // Orders asks for its own inbox. It owes nothing on this exchange, so the replay finds
        // nothing for it and its own question is not returned.
        var toAsker = (await resuming.DrainInboxAsync(
            Orders, T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Empty(toAsker);
    }

    [Fact]
    public async Task Draining_an_empty_inbox_is_not_an_error()
    {
        var drained = (await _broker.DrainInboxAsync(
            Billing, T0, cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Empty(drained);
    }

    [Fact]
    public async Task Draining_returns_only_what_is_addressed_to_the_caller()
    {
        var exchange = await Ask(Orders, Billing, "q");
        await Send(MessageType.Question, Orders, Billing, exchange.Id, "gross or net?");

        var drained = (await _broker.DrainInboxAsync(
            Orders, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Empty(drained);
    }
}
