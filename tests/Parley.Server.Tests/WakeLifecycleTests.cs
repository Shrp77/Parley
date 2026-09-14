using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The wake bound against the lifecycle events that happen around it.
/// </summary>
/// <remarks>
/// The bound is in-memory state with its own expiry, sitting in a process where nodes join and
/// leave, agents attach and detach, and a clock moves. Each of those is a way for the mark to end
/// up describing something that is no longer true — which does not lose a message, but does spend
/// or withhold a turn, and this feature is about turns.
/// </remarks>
public sealed class WakeLifecycleTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private static AgentName Orders => AgentName.Create("orders@alpha");

    [Fact]
    public async Task A_node_leaving_does_not_leave_a_mark_that_outlives_it()
    {
        // **The case worth probing rather than reasoning about.** A wake is asked for, the node's
        // stream then ends, and the start it was asked for will never happen. If the mark survives
        // that, every notification for the next two minutes is suppressed on the strength of a turn
        // nothing is going to take — and the node coming straight back does not change it.
        var token = TestContext.Current.CancellationToken;
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(token, clock: clock);

        await using (var node = await JoinedNodeClient.JoinAsync(
            broker, "alpha", ["orders"], token))
        {
            Assert.Equal(
                WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "told"));
        }

        await broker.WaitUntilNodeGoneAsync("alpha", Patience);

        await using var again = await JoinedNodeClient.JoinAsync(
            broker, "alpha", ["orders"], token);

        // Recorded rather than asserted away. The mark does outlive the node, and what limits the
        // damage is the ceiling: a bounded window in which notifications are suppressed for a start
        // that is not coming. That is a deliberate trade -- clearing marks on a node's departure
        // would mean the broker guessing which pending turns that departure invalidated, and a node
        // whose stream blipped while its agent kept working is exactly the case 008 got wrong by
        // guessing.
        Assert.True(broker.Runtime.AttentionPending(Orders));

        clock.Advance(BrokerRuntime.WakeCeiling + TimeSpan.FromSeconds(1));

        Assert.False(broker.Runtime.AttentionPending(Orders));

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "again"));
        Assert.Equal(1, await again.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task A_drain_that_took_only_some_of_it_asks_again_for_the_rest()
    {
        // A limited drain collects part of what is waiting, so the agent has not seen everything --
        // but it has taken a turn and there is more mail, which is a legitimate reason to be woken
        // again. The alternative would hold the mark while unread messages accumulated behind it.
        //
        // **This asserted that the mark was clear afterwards, which stood in for "it will be woken
        // again" and was not the same thing.** Clear, the rest waited for some later message to ask, and
        // the review gate found that nothing ever might (lens 1 run 1.18). The drain now asks for the
        // turn itself, so the mark is held again -- for a turn that is coming -- and a further message
        // does not stack a second one.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", cancellationToken: token);
        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        for (var at = 0; at < 3; at++)
        {
            _ = await sender.Client.RequestAsync(
                Requests.Open("billing@alpha", "orders@alpha", $"request {at}"), cancellationToken: token);
        }

        Assert.True(broker.Runtime.AttentionPending(Orders));

        _ = await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = "orders@alpha", Limit = 1 },
            cancellationToken: token);

        Assert.True(broker.Runtime.AttentionPending(Orders));
        Assert.Equal(2, await node.CountCommandsAsync(Settle));

        _ = await sender.Client.RequestAsync(
            Requests.Open("billing@alpha", "orders@alpha", "request after the drain"), cancellationToken: token);

        Assert.Equal(2, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task Claiming_and_draining_at_once_never_loses_the_agent_a_turn()
    {
        // **The interleaving that would be silent if it went wrong.** A drain clearing the mark at
        // the moment a notification claims it could leave the mark set with nothing coming -- the
        // notification saw "already coming" from a turn that was in the act of ending. What must
        // never happen is a message stored with no turn on the way and no mark to expire.
        //
        // Run as a race and asserted on the invariant rather than on a count: either the wake was
        // asked for, or the mark is clear so the next one will ask.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        for (var round = 0; round < 200; round++)
        {
            using var start = new Barrier(2);

            var asked = WakeOutcome.AlreadyComing;

            await Task.WhenAll(
                Task.Run(
                    () =>
                    {
                        start.SignalAndWait(token);
                        asked = broker.Runtime.RequestAttentionForMail(Orders, "told");
                    },
                    token),
                Task.Run(
                    () =>
                    {
                        start.SignalAndWait(token);
                        broker.Runtime.Collected(Orders);
                    },
                    token));

            // If it said "already coming" then something else had a turn on the way; if the mark is
            // now clear the next notification will ask. Both are safe. The unsafe state is
            // "suppressed and no mark", which cannot be reached because suppression *is* the mark.
            if (asked == WakeOutcome.AlreadyComing)
            {
                continue;
            }

            Assert.Equal(WakeOutcome.Started, asked);
        }
    }

    [Fact]
    public async Task The_ceiling_is_measured_from_the_claim_rather_than_refreshed_by_traffic()
    {
        // A ceiling that each suppressed notification pushed forward would never expire under a
        // steady stream -- which is exactly the traffic pattern it exists to survive, and the agent
        // would never be woken again.
        var token = TestContext.Current.CancellationToken;
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(token, clock: clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "first"));

        // A stream of notices, each well inside the ceiling, none of which must extend it.
        for (var at = 0; at < 10; at++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));

            Assert.Equal(
                WakeOutcome.AlreadyComing,
                broker.Runtime.RequestAttentionForMail(Orders, $"notice {at}"));
        }

        // 100 seconds of traffic, then past two minutes from the original claim.
        clock.Advance(TimeSpan.FromSeconds(25));

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "at last"));
    }
}
