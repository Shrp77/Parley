using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The person's line and what the agent settles, over a real socket (SPEC.md §5.6, quickstart steps 1–5).
/// </summary>
/// <remarks>
/// <b>Everything is read back through the wire</b> — what the agent is told on collecting, what its reply's settles did,
/// and what the person's surface then sees — because a row nobody can read through the protocol is not evidence the
/// protocol records anything.
/// </remarks>
public sealed class SettlingOverTheWireTests
{
    private const string Conversation = "invoice-export";

    private static readonly string Person = PersonSeat.On(ProjectName.Create("test")).Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> AskAsync(AttachedClient from, string to, string text, string conversation = Conversation)
    {
        var sent = Assert.Single((await from.Client.RequestAsync(
            Requests.Open(from.Name, to, text, conversation), cancellationToken: Ct)).Outcomes);

        Assert.True(sent.Ok, sent.Rejection.ToString());

        return sent.ExchangeId;
    }

    [Fact]
    public async Task The_agent_is_told_its_open_questions_beside_the_persons_request_and_settles_one()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: PersonSeat.Adapter);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var first = await AskAsync(orders, Person, "which invoice schema should the export target?");
        var second = await AskAsync(orders, Person, "gross or net?");
        var line = await AskAsync(seat, orders.Name, "schema 12, and what is it for?");

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);
        var delivered = Assert.Single(inbox.Messages, m => m.Message.ExchangeId == line);

        Assert.Equal([first, second], delivered.Settleable.Select(q => q.ExchangeId));
        Assert.StartsWith("which invoice schema", delivered.Settleable[0].Asked, StringComparison.Ordinal);
        Assert.Equal(0, delivered.SettleableLeftOut);

        var reply = Requests.Result(orders.Name, line);
        reply.Settles.Add(first);

        var said = await orders.Client.ResultAsync(reply, cancellationToken: Ct);

        Assert.True(said.Ok, said.Refusal.ToString());
        Assert.Equal([first], said.Settled);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = Conversation }, cancellationToken: Ct);

        var settled = export.Exchanges.Single(e => e.ExchangeId == first);

        Assert.Equal(Wire.ExchangeOutcome.Settled, settled.Outcome);
        Assert.Equal(line, settled.SettledBy);
        Assert.Equal(Wire.ExchangeState.Open, export.Exchanges.Single(e => e.ExchangeId == second).State);

        // What the person's surface reads as waiting on them: one of orders' questions, not two.
        var waiting = await broker.ConnectAsOperator().DescribeWaitingAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.Equal([second], waiting.Edges.Where(e => e.WaitedOn == Person).Select(e => e.ExchangeId));
    }

    [Fact]
    public async Task A_request_handed_back_carries_what_the_person_said_since_the_agent_asked_back()
    {
        // Must fail against a broker that re-delivers the person's first words alone: the agent then asks the same
        // question again, which is what happened in a live conversation.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: PersonSeat.Adapter);

        long first;
        long answer;

        await using (var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct))
        {
            first = await AskAsync(seat, orders.Name, "summarize this");

            var firstInbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

            Assert.Empty(Assert.Single(firstInbox.Messages, m => m.Message.ExchangeId == first).Since);

            var asked = await orders.Client.ClarifyAsync(
                new Wire.ClarifyRequest
                {
                    From = orders.Name,
                    ExchangeId = first,
                    ResponderModel = "claude-opus-5",
                    PayloadJson = """{"text":"summarize what?"}"""
                },
                cancellationToken: Ct);

            Assert.True(asked.Ok, asked.ToString());

            answer = await AskAsync(seat, orders.Name, "the support address decision, D2");

            var reply = Requests.Result(orders.Name, answer);
            reply.Settles.Add(first);

            Assert.True((await orders.Client.ResultAsync(reply, cancellationToken: Ct)).Ok);
        }

        // The agent's next session: attaching gives back the work it still owes, which is the Request handed back. Only
        // once the broker has seen the first session end, because work is given back only to an agent with no session
        // attached, and a close still in flight counts as one.
        await broker.WaitUntilDetachedAsync("orders@alpha", TimeSpan.FromSeconds(10));

        await using var next = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var again = await next.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = next.Name }, cancellationToken: Ct);
        var handedBack = Assert.Single(again.Messages, m => m.Message.ExchangeId == first);
        var since = Assert.Single(handedBack.Since);

        Assert.Equal(answer, since.ExchangeId);
        Assert.Contains("the support address decision, D2", since.Envelope, StringComparison.Ordinal);
        Assert.Contains("[parley:", since.Envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_request_from_the_person_carries_the_list()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        await AskAsync(orders, Person, "which invoice schema?");
        var fromBilling = await AskAsync(billing, orders.Name, "gross or net?", "billing-work");

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

        Assert.Empty(Assert.Single(inbox.Messages, m => m.Message.ExchangeId == fromBilling).Settleable);
    }

    [Fact]
    public async Task A_bad_name_comes_back_with_its_problem_and_nothing_is_written()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: PersonSeat.Adapter);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var question = await AskAsync(orders, Person, "which invoice schema?");
        var line = await AskAsync(seat, orders.Name, "12");

        var reply = Requests.Result(orders.Name, line);
        reply.Settles.Add(question);
        reply.Settles.Add(line);

        var said = await orders.Client.ResultAsync(reply, cancellationToken: Ct);

        Assert.False(said.Ok);
        Assert.Equal(Wire.TransitionError.CannotSettle, said.Refusal);

        var refused = Assert.Single(said.CannotSettle);

        Assert.Equal(line, refused.ExchangeId);
        Assert.Equal(Wire.SettleProblem.NotTheirMove, refused.Problem);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = Conversation }, cancellationToken: Ct);

        Assert.All(export.Exchanges, e => Assert.Equal(Wire.ExchangeState.Open, e.State));
        Assert.DoesNotContain(export.Messages, m => m.Type == Wire.MessageType.Result);
    }

    [Fact]
    public async Task The_persons_own_request_comes_back_to_the_agent()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: PersonSeat.Adapter);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var persons = await AskAsync(seat, orders.Name, "export last month's invoices");

        var asked = await orders.Client.ClarifyAsync(new Wire.ClarifyRequest
        {
            From = orders.Name,
            ExchangeId = persons,
            ResponderModel = "claude-opus-5",
            PayloadJson = """{"text":"which customers?"}"""
        }, cancellationToken: Ct);

        Assert.True(asked.Ok, asked.Refusal.ToString());

        var line = await AskAsync(seat, orders.Name, "all of them");

        var reply = Requests.Result(orders.Name, line);
        reply.Settles.Add(persons);

        var said = await orders.Client.ResultAsync(reply, cancellationToken: Ct);

        Assert.True(said.Ok, said.Refusal.ToString());
        Assert.Equal([persons], said.HandedBack);

        var waiting = await broker.ConnectAsOperator().DescribeWaitingAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.Contains(waiting.Edges, e => e.ExchangeId == persons && e.WaitedOn == orders.Name);
    }
}

