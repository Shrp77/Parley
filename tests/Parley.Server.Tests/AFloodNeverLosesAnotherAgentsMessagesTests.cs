using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That one collection carries a bounded amount of messages and leaves the rest waiting, counted (SPEC.md §10.1).
/// </summary>
/// <remarks>
/// A message is stamped delivered as it is handed over. An unbounded collection of a few hundred long Requests was
/// refused at the adapter's message size, so every message in it, a third agent's included, was marked collected and
/// never seen: one agent could empty another's inbox by writing to it.
/// </remarks>
public sealed class AFloodNeverLosesAnotherAgentsMessagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_flood_of_long_requests_leaves_every_message_collectable()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, "inventory@test", cancellationToken: Ct);

        var asked = Assert.Single((await inventory.Client.RequestAsync(
            Requests.Open(inventory.Name, orders.Name, "what is the export column called?"), cancellationToken: Ct)).Outcomes);
        Assert.True(asked.Ok, asked.Rejection.ToString());

        // No pair cap by default, and each Request under the 8,000-character cap: together far more than one reply carries.
        const int Flood = 400;

        for (var n = 0; n < Flood; n++)
        {
            var text = n.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string('x', 7_000);
            var sent = Assert.Single((await billing.Client.RequestAsync(
                Requests.Open(billing.Name, orders.Name, text), cancellationToken: Ct)).Outcomes);
            Assert.True(sent.Ok, $"request {n}: {sent.Rejection}");
        }

        var seen = new HashSet<long>();
        Wire.InboxReply reply;
        var collections = 0;

        // What parley_inbox sends by default: no limit.
        do
        {
            reply = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);
            collections++;

            foreach (var message in reply.Messages)
            {
                Assert.True(seen.Add(message.Message.ExchangeId), $"exchange {message.Message.ExchangeId} handed over twice");
            }

            Assert.True(collections <= Flood + 1, "collections never ended");
        }
        while (reply.MessagesWaiting > 0);

        Assert.True(collections > 1, "one collection carried the whole flood");
        Assert.Contains(asked.ExchangeId, seen);
        Assert.Equal(Flood + 1, seen.Count);
    }
}
