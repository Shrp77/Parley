using Parley.Core;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// The bound against a clock that does not only move forwards.
/// </summary>
/// <remarks>
/// <para>
/// The ceiling is the only part of this feature measured in time, and everything measured in time
/// on a real machine meets a clock being corrected. An NTP step backwards is ordinary; a virtual
/// machine resuming from a snapshot is a large one.
/// </para>
/// <para>
/// Probed rather than reasoned about, because the arithmetic reads as though it handles it and does
/// not: an elapsed time that has gone negative is still less than the ceiling.
/// </para>
/// </remarks>
public sealed class WakeClockTests
{
    private static AgentName Orders => AgentName.Create("orders@alpha");

    [Fact]
    public async Task A_clock_stepping_backwards_does_not_suspend_waking_until_it_catches_up()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(token, clock: clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "told"));

        // The correction. An hour backwards is a snapshot restore rather than a leap second, and it
        // is the shape that makes the arithmetic's answer obvious.
        clock.Advance(TimeSpan.FromHours(-1));

        Assert.False(
            broker.Runtime.AttentionPending(Orders),
            "a wake claimed in what is now the future kept the agent suppressed");

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "again"));
    }

    [Fact]
    public async Task A_clock_that_does_not_move_still_expires_nothing_early()
    {
        // The other direction of the same check: a frozen clock must keep the mark, or the bound
        // would be defeated by a machine whose time source has stalled.
        var token = TestContext.Current.CancellationToken;
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(token, clock: clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "told"));

        for (var at = 0; at < 5; at++)
        {
            Assert.Equal(
                WakeOutcome.AlreadyComing, broker.Runtime.RequestAttentionForMail(Orders, "again"));
        }
    }

    [Fact]
    public async Task Expiring_and_claiming_at_once_never_drops_the_newer_claim()
    {
        // The expiry removes by key *and* value, so a claim landing between the read and the remove
        // is not thrown away by it. Removing by key alone would leave the agent unsuppressed with a
        // turn on the way -- a duplicate turn, bought silently.
        var token = TestContext.Current.CancellationToken;
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(token, clock: clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        for (var round = 0; round < 200; round++)
        {
            broker.Runtime.Collected(Orders);

            Assert.Equal(
                WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "claim"));

            clock.Advance(BrokerRuntime.WakeCeiling + TimeSpan.FromSeconds(1));

            using var start = new Barrier(2);
            var second = WakeOutcome.AlreadyComing;

            await Task.WhenAll(
                Task.Run(
                    () =>
                    {
                        start.SignalAndWait(token);
                        _ = broker.Runtime.AttentionPending(Orders);
                    },
                    token),
                Task.Run(
                    () =>
                    {
                        start.SignalAndWait(token);
                        second = broker.Runtime.RequestAttentionForMail(Orders, "re-claim");
                    },
                    token));

            // Whoever won, the invariant is the same: if a turn was asked for, the mark is set.
            if (second == WakeOutcome.Started)
            {
                Assert.True(
                    broker.Runtime.AttentionPending(Orders),
                    $"round {round}: a turn was asked for and the mark was dropped by the expiry");
            }
        }
    }
}
