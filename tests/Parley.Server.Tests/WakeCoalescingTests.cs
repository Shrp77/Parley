using Parley.Core;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// The bound that makes waking a notification's recipient affordable.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not an optimisation and these are not performance tests.</b> SPEC.md §17 question 15
/// refuses the wake outright on the grounds that it would be unbounded, so the coalescing is the
/// condition on which the feature ships at all. If these fail, the wake goes back.
/// </para>
/// <para>
/// <b>The mark is cleared by a drain and not by an attach, and that was established by reading the
/// node rather than by reasoning.</b> <c>NodeCommand.StartAsync</c> dispatches a turn when the
/// resident is already alive, so the node refuses a duplicate <em>process</em> and cannot refuse a
/// duplicate <em>turn</em>. An attach-keyed mark would clear a second after the first notification
/// and leave the other fifty-nine to buy a turn apiece — bounding nothing while appearing to work.
/// </para>
/// <para>
/// Driven through <see cref="BrokerRuntime"/> rather than over the wire on purpose. These are about
/// the bound alone; that a notification consults it is <c>NotifyWakesTests</c>.
/// </para>
/// </remarks>
public sealed class WakeCoalescingTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static AgentName Orders => AgentName.Create("orders@alpha");

    [Fact]
    public async Task A_wake_marks_attention_pending()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.False(broker.Runtime.AttentionPending(Orders));

        Assert.True(broker.Runtime.RequestAttention(Orders, "a probe"));
        Assert.True(broker.Runtime.AttentionPending(Orders));
    }

    [Fact]
    public async Task A_drain_clears_it()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        _ = broker.Runtime.RequestAttention(Orders, "a probe");

        broker.Runtime.Collected(Orders);

        Assert.False(broker.Runtime.AttentionPending(Orders));
    }

    [Fact]
    public async Task The_ceiling_expires_it_so_a_failed_start_does_not_silence_waking()
    {
        // The failure valve. A wake that never produced a drain -- a launch that died, a process
        // that attached and went away -- must not suppress every later notification for ever.
        var token = TestContext.Current.CancellationToken;
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(token, clock: clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        _ = broker.Runtime.RequestAttention(Orders, "a start that will not land");

        Assert.True(broker.Runtime.AttentionPending(Orders));

        clock.Advance(BrokerRuntime.WakeCeiling + TimeSpan.FromSeconds(1));

        Assert.False(broker.Runtime.AttentionPending(Orders));
    }

    [Fact]
    public async Task Mail_asks_for_nothing_while_attention_is_already_pending()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "told"));
        Assert.Equal(
            WakeOutcome.AlreadyComing, broker.Runtime.RequestAttentionForMail(Orders, "told again"));

        // One command, not two. The count is the assertion: `WaitForCommandAsync` answers "did one
        // arrive", which cannot tell one from sixty.
        Assert.Equal(1, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task A_questions_wake_stops_a_notification_stacking_a_second_turn()
    {
        // FR-008a. Every wake marks; only mail consults the mark. The common case this is for is
        // "ask a peer, then tell it something" -- two messages, one reading, and without this two
        // turns charged to whoever hosts the peer.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.True(broker.Runtime.RequestAttention(Orders, "asked a question"));

        Assert.Equal(
            WakeOutcome.AlreadyComing, broker.Runtime.RequestAttentionForMail(Orders, "and told it"));

        Assert.Equal(1, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task After_a_drain_the_next_notification_is_free_to_wake_again()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "first"));

        broker.Runtime.Collected(Orders);

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(Orders, "second"));

        Assert.Equal(2, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task An_agent_no_node_can_start_is_unreachable_and_marks_nothing()
    {
        // Holding the mark here would suppress every notification for the next two minutes on the
        // strength of a turn that was never asked for -- and this is the case where the recipient
        // most needs the next one to try.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);

        Assert.Equal(
            WakeOutcome.Unreachable, broker.Runtime.RequestAttentionForMail(Orders, "told"));

        Assert.False(broker.Runtime.AttentionPending(Orders));

        Assert.Equal(
            WakeOutcome.Unreachable, broker.Runtime.RequestAttentionForMail(Orders, "told again"));
    }

    [Fact]
    public async Task An_agent_on_a_machine_with_no_node_is_unreachable()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        Assert.Equal(
            WakeOutcome.Unreachable,
            broker.Runtime.RequestAttentionForMail(AgentName.Create("orders@nowhere"), "told"));
    }

    [Fact]
    public async Task Sixty_notifications_at_once_ask_the_node_once()
    {
        // **It has to actually race.** Sixty sequential calls exercise a path that says nothing
        // about the concurrent one, and the check-then-act this replaced passed the sequential
        // version perfectly: every one of sixty interleaved callers would have read "nothing
        // pending" and every one would have asked.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        using var start = new Barrier(60);
        var outcomes = new WakeOutcome[60];

        await Task.WhenAll(Enumerable.Range(0, 60).Select(at => Task.Run(
            () =>
            {
                start.SignalAndWait(token);
                outcomes[at] = broker.Runtime.RequestAttentionForMail(Orders, $"notice {at}");
            },
            token)));

        Assert.Equal(1, outcomes.Count(one => one == WakeOutcome.Started));
        Assert.Equal(59, outcomes.Count(one => one == WakeOutcome.AlreadyComing));

        Assert.Equal(1, await node.CountCommandsAsync(Settle));
    }
}
