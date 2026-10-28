using Parley.Core;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That a message stored while its receiver is collecting still asks for a turn.
/// </summary>
/// <remarks>
/// <para>
/// The drain must not clear the wake bound as a separate step, or a
/// Request completing between the two would find the bound held, ask for nothing, and lose it to a collection that had
/// never read it: with a joined node, one command in all, nothing pending, the Request
/// uncollected. An attached adapter only records a stream notice; a turn is what makes it collect.
/// </para>
/// <para>
/// Through <see cref="BrokerRuntime.CollectAsync{T}"/>, the method the inbox handler calls, with the arrival inside
/// the drain delegate: that is the one place a test can stand between the read and whatever follows it without
/// racing threads.
/// </para>
/// </remarks>
public sealed class WakeDuringCollectionTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static AgentName Orders => AgentName.Create("orders@alpha");

    [Fact]
    public async Task A_message_arriving_after_the_drain_read_asks_for_its_own_turn()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        // Started by its node (SPEC.md §11.7), which is what makes it a resident a node gives turns to.
        await using var resident = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token, startedBy: node);

        // The turn that is collecting was asked for by an earlier message.
        Assert.NotEqual(WakeOutcome.AlreadyComing, broker.Runtime.RequestAttentionForMail(Orders, "first"));

        WakeOutcome? arriving = null;

        var (_, hold) = await broker.Runtime.CollectAsync(
            Orders,
            async (b, _, ct) =>
            {
                var drained = await b.CollectInboxAsync(Orders, broker.Runtime.InboxHolds, null, ct);

                // What a Request stored at this instant does next.
                arriving = broker.Runtime.RequestAttentionForMail(Orders, "second");

                return drained;
            },
            token);
        broker.Runtime.InboxHolds.Release(hold);

        Assert.NotNull(arriving);
        Assert.NotEqual(WakeOutcome.AlreadyComing, arriving);

        // Its claim survives the collection that did not read it, so nothing else is needed for the turn to come.
        Assert.True(broker.Runtime.AttentionPending(Orders));
        Assert.Equal(2, await node.CountCommandsAsync(2, Settle));
    }

    [Fact]
    public async Task A_burst_before_the_collection_still_costs_one_turn()
    {
        // The bound this moves must still hold for what it was for: messages that land before anybody collects.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        for (var at = 0; at < 20; at++)
        {
            _ = broker.Runtime.RequestAttentionForMail(Orders, $"message {at}");
        }

        // Waited for before counting: a fixed settle alone read 0 once under the full suite's load, a command that had
        // not reached the node yet rather than one never sent — the same build passed six runs alone.
        _ = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await node.CountCommandsAsync(1, Settle));
    }
}
