using System.Text.Json.Nodes;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That messages a limited collection leaves behind still have a turn coming.
/// </summary>
/// <remarks>
/// <para>
/// Found by the review gate, lens 1 run 1.18. <c>parley_inbox</c> offers a limit, and a collection freed the wake bound
/// whatever it left: probed over a loopback broker with a joined node, orders collected one of two Requests and
/// detached, nothing was pending, the node had been asked once, and the second Request waited with no turn coming.
/// </para>
/// <para>
/// Through the real <c>DrainInbox</c> handler, because the decision to ask again lives there.
/// </para>
/// </remarks>
public sealed class LimitedCollectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static AgentName Orders => AgentName.Create("orders@alpha");

    private static Wire.OpenRequest Request(string text) =>
        new()
        {
            From = "billing@alpha",
            To = "orders@alpha",
            ResponderModel = "test",
            PayloadJson = new JsonObject { ["ask"] = text }.ToJsonString(),
            ConversationId = "c1"
        };

    private static async Task<(LoopbackBroker Broker, JoinedNodeClient Node, AttachedClient Sender)> TwoWaitingAsync(
        CancellationToken token)
    {
        var broker = await LoopbackBroker.StartAsync(token);
        var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await using (await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync("orders@alpha", Patience);

        var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        foreach (var text in new[] { "first", "second" })
        {
            Assert.True(Assert.Single((await sender.Client.RequestAsync(Request(text), cancellationToken: token)).Outcomes).Ok);
        }

        return (broker, node, sender);
    }

    [Fact]
    public async Task A_collection_limited_to_fewer_than_are_waiting_asks_again_for_the_rest()
    {
        var token = TestContext.Current.CancellationToken;
        var (broker, node, sender) = await TwoWaitingAsync(token);

        await using (broker)
        await using (node)
        await using (sender)
        {
            // The resident its node started (SPEC.md §11.7): one opened outside Parley is asked for nothing at all.
            await using (var resident = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token, startedBy: node))
            {
                var reply = await resident.Client.DrainInboxAsync(
                    new Wire.DrainInboxRequest { Agent = "orders@alpha", Limit = 1 }, cancellationToken: token);

                Assert.Single(reply.Messages);
            }

            Assert.True(broker.Runtime.AttentionPending(Orders));
            Assert.Equal(2, await node.CountCommandsAsync(2, Settle));
        }
    }

    [Fact]
    public async Task A_collection_that_takes_everything_asks_for_nothing_more()
    {
        var token = TestContext.Current.CancellationToken;
        var (broker, node, sender) = await TwoWaitingAsync(token);

        await using (broker)
        await using (node)
        await using (sender)
        {
            // Started by its node, as above, so that asking for nothing more is a decision rather than §11.7's silence.
            await using (var resident = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token, startedBy: node))
            {
                var reply = await resident.Client.DrainInboxAsync(
                    new Wire.DrainInboxRequest { Agent = "orders@alpha", Limit = 2 }, cancellationToken: token);

                Assert.Equal(2, reply.Messages.Count);
            }

            Assert.False(broker.Runtime.AttentionPending(Orders));
            Assert.Equal(1, await node.CountCommandsAsync(1, Settle));
        }
    }
}
