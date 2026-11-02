using System.Globalization;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That one agent writing to another faster than it collects holds back nobody else's messages (SPEC.md §7.3, §10.1).
/// </summary>
/// <remarks>
/// One collection carries a bounded amount and debts come first. In plain age order a steady stream of Requests from
/// one sender kept every other sender's Requests, and every Result, out of every collection; and closure polls, listed
/// with no bound, could make a collection too large to arrive after its messages were stamped delivered.
/// </remarks>
[Integration]
public sealed class AFloodHoldsNobodyElseBackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Long(int n) => n.ToString(CultureInfo.InvariantCulture) + new string('x', 7_900);

    [Fact]
    public async Task A_result_rides_along_through_a_steady_flood_of_requests()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, "inventory@test", cancellationToken: Ct);

        var asked = Assert.Single((await orders.Client.RequestAsync(
            Requests.Open(orders.Name, inventory.Name, "what is the export column called?"), cancellationToken: Ct)).Outcomes);
        Assert.True(asked.Ok, asked.Rejection.ToString());
        _ = await inventory.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = inventory.Name }, cancellationToken: Ct);
        _ = await inventory.Client.ResultAsync(Requests.Result(inventory.Name, asked.ExchangeId), cancellationToken: Ct);

        var n = 0;

        // Nine long Requests before each collection: more than one collection carries, every time.
        for (var round = 0; round < 2; round++)
        {
            for (var k = 0; k < 9; k++)
            {
                var sent = Assert.Single((await billing.Client.RequestAsync(
                    Requests.Open(billing.Name, orders.Name, Long(n++)), cancellationToken: Ct)).Outcomes);
                Assert.True(sent.Ok, sent.Rejection.ToString());
            }

            var reply = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

            if (reply.Messages.Any(m => m.Message.ExchangeId == asked.ExchangeId))
            {
                Assert.Equal(0, round);
                return;
            }
        }

        Assert.Fail("inventory's Result was held back by billing's flood");
    }

    [Fact]
    public async Task A_collection_limited_to_fewer_than_the_debts_still_carries_the_result()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, "inventory@test", cancellationToken: Ct);

        var asked = Assert.Single((await orders.Client.RequestAsync(
            Requests.Open(orders.Name, inventory.Name, "what is the export column called?"), cancellationToken: Ct)).Outcomes);
        Assert.True(asked.Ok, asked.Rejection.ToString());
        _ = await inventory.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = inventory.Name }, cancellationToken: Ct);
        _ = await inventory.Client.ResultAsync(Requests.Result(inventory.Name, asked.ExchangeId), cancellationToken: Ct);

        for (var n = 0; n < 3; n++)
        {
            var sent = Assert.Single((await billing.Client.RequestAsync(
                Requests.Open(billing.Name, orders.Name, "q" + n.ToString(CultureInfo.InvariantCulture)), cancellationToken: Ct)).Outcomes);
            Assert.True(sent.Ok, sent.Rejection.ToString());
        }

        // A limit cuts the list before the debts end; the Result rides along beside the two asked for.
        var reply = await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = orders.Name, Limit = 2 }, cancellationToken: Ct);

        Assert.Equal(3, reply.Messages.Count);
        Assert.Contains(reply.Messages, m => m.Message.ExchangeId == asked.ExchangeId);
    }

    [Fact]
    public async Task Another_senders_request_is_taken_in_turn_with_a_flood()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, "inventory@test", cancellationToken: Ct);

        for (var n = 0; n < 30; n++)
        {
            var sent = Assert.Single((await billing.Client.RequestAsync(
                Requests.Open(billing.Name, orders.Name, Long(n)), cancellationToken: Ct)).Outcomes);
            Assert.True(sent.Ok, sent.Rejection.ToString());
        }

        // Sent after the whole flood, so in age order it would wait behind thirty Requests and four collections.
        var later = Assert.Single((await inventory.Client.RequestAsync(
            Requests.Open(inventory.Name, orders.Name, "what is the export column called?"), cancellationToken: Ct)).Outcomes);
        Assert.True(later.Ok, later.Rejection.ToString());

        var reply = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

        Assert.True(reply.MessagesWaiting > 0, "one collection carried the whole flood");
        Assert.Contains(reply.Messages, m => m.Message.ExchangeId == later.ExchangeId);
    }

    [Fact]
    public async Task Closure_polls_are_listed_two_hundred_at_a_time_and_the_rest_counted()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);

        const int Conversations = 230;
        var opened = new List<string>();

        for (var n = 0; n < Conversations; n++)
        {
            var sent = Assert.Single((await billing.Client.RequestAsync(
                Requests.Open(billing.Name, orders.Name, "q" + n.ToString(CultureInfo.InvariantCulture)), cancellationToken: Ct)).Outcomes);
            Assert.True(sent.Ok, sent.Rejection.ToString());
            opened.Add(sent.ConversationId);
        }

        Wire.InboxReply reply;

        do
        {
            reply = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);
        }
        while (reply.MessagesWaiting > 0);

        foreach (var conversation in opened)
        {
            _ = await billing.Client.ConcludeAsync(
                new Wire.ConcludeRequest { From = billing.Name, ConversationId = conversation, Vote = Wire.Vote.Done },
                cancellationToken: Ct);
        }

        reply = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

        Assert.Equal(200, reply.ClosurePolls.Count);
        Assert.Equal(Conversations - 200, reply.ClosurePollsNotListed);

        // Listed, not taken: a poll stays until it is answered, so the next collection lists the same oldest ones.
        var again = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);
        Assert.Equal(reply.ClosurePolls.Select(p => p.ConversationId), again.ClosurePolls.Select(p => p.ConversationId));
    }
}
