using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Adversarial probes against the wake and its bound.
/// </summary>
/// <remarks>
/// Written to be admitted rather than read. The highest-value findings in this repository came from
/// writing the malicious case and watching it succeed, and a wake is a new way to make somebody
/// else's machine spend money — so the question is not whether the bound is written correctly but
/// whether it can be got round.
/// </remarks>
public sealed class WakeProbeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private static Wire.OpenRequest Notice(string from, string to, string text, string? conversation = null) =>
        Requests.Open(from, to, text, conversation);

    private static async Task RegisterAndCloseAsync(
        LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    [Fact]
    public async Task Many_senders_at_once_cannot_defeat_the_bound_over_the_wire()
    {
        // The runtime-level race is covered by WakeCoalescingTests. This is the same attack through
        // gRPC, where the handlers really are concurrent and the claim is reached from several
        // request threads rather than from Task.Run.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var senders = new List<AttachedClient>();

        try
        {
            for (var at = 0; at < 8; at++)
            {
                senders.Add(await AttachedClient.AttachAsync(
                    broker, $"peer{at}@alpha", cancellationToken: token));
            }

            using var start = new Barrier(senders.Count);

            await Task.WhenAll(senders.Select((sender, at) => Task.Run(
                async () =>
                {
                    start.SignalAndWait(token);

                    // Distinct text per sender, so deduplication is not what bounds this.
                    _ = await sender.Client.RequestAsync(
                        Notice($"peer{at}@alpha", "orders@alpha", $"from {at}"),
                        cancellationToken: token);
                },
                token)));

            Assert.Equal(1, await node.CountCommandsAsync(1, Settle));
        }
        finally
        {
            foreach (var sender in senders)
            {
                await sender.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Varying_the_conversation_does_not_buy_another_turn()
    {
        // The obvious way round a per-recipient bound would be to make each notice look new to
        // whatever the bound keys on. It keys on the recipient and nothing else, so this must not
        // work -- and if it ever did, one peer could spend a machine's budget by counting upwards.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        for (var at = 0; at < 20; at++)
        {
            _ = await sender.Client.RequestAsync(
                Notice("billing@alpha", "orders@alpha", $"notice {at}", $"conversation-{at}"),
                cancellationToken: token);
        }

        Assert.Equal(1, await node.CountCommandsAsync(1, Settle));
    }

    [Fact]
    public async Task An_agent_notifying_itself_cannot_spin_itself_a_turn_per_drain()
    {
        // **The loop worth checking for.** An agent that can wake itself, and whose own drain is
        // what frees the next wake, has a cycle whose every revolution is a model taking a turn on
        // somebody's machine at somebody's expense. Nothing in §7's cycle detection covers it: that
        // admits cycles between participants, and says nothing about one participant alone.
        //
        // **The cycle is closed one step earlier than expected, and that is the finding.** The
        // broker refuses an agent addressing itself at all, so the loop cannot start — the probe
        // was written expecting to have to count turns and never got that far. Pinned here because
        // nothing else in the suite connects that refusal to this consequence: it was written
        // against messaging, and it is now also what stops an agent spending its host's budget in
        // a loop of its own making.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", cancellationToken: token);

        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(
            async () => await orders.Client.RequestAsync(
                Notice("orders@alpha", "orders@alpha", "round 0"), cancellationToken: token));

        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, refused.StatusCode);

        // Nothing was claimed and nothing was asked of the node, so a refused self-notice cannot
        // suppress a legitimate one from a peer either.
        Assert.False(broker.Runtime.AttentionPending(Parley.Core.AgentName.Create("orders@alpha")));
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task A_failed_wake_does_not_take_a_successful_one_with_it()
    {
        // The release path. When a wake cannot be asked for, the claim is given back -- and it must
        // give back only its own, or a notification that failed would erase the mark a question had
        // just set and buy a duplicate turn.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);
        await RegisterAndCloseAsync(broker, "docs@alpha", token);

        var orders = Parley.Core.AgentName.Create("orders@alpha");
        var docs = Parley.Core.AgentName.Create("docs@alpha");

        // `docs` is registered and not catalogued, so its wake fails and releases.
        Assert.Equal(
            Parley.Core.WakeOutcome.Unreachable,
            broker.Runtime.RequestAttentionForMail(docs, "told"));

        Assert.False(broker.Runtime.AttentionPending(docs));

        // And the neighbour's mark is untouched.
        Assert.Equal(
            Parley.Core.WakeOutcome.Started,
            broker.Runtime.RequestAttentionForMail(orders, "told"));

        Assert.True(broker.Runtime.AttentionPending(orders));
    }

    [Fact]
    public async Task A_drain_by_one_agent_does_not_free_another_agents_wake()
    {
        // The mark is keyed by name, and a drain that cleared more than its own would let any agent
        // buy a peer an extra turn by reading its own mail.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(
            broker, "alpha", ["orders", "billing"], token);

        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var orders = Parley.Core.AgentName.Create("orders@alpha");

        Assert.Equal(
            Parley.Core.WakeOutcome.Started,
            broker.Runtime.RequestAttentionForMail(orders, "told"));

        _ = await billing.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: token);

        Assert.True(
            broker.Runtime.AttentionPending(orders),
            "one agent draining cleared another agent's pending wake");
    }

    [Fact]
    public async Task A_retired_recipient_is_refused_before_anything_is_claimed()
    {
        // Order matters: a claim taken before the refusal is decided would suppress the next
        // legitimate notification for two minutes on the strength of a message that never landed.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        _ = await broker.ConnectAsOperator().RetireAsync(
            new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var reply = await sender.Client.RequestAsync(
            Notice("billing@alpha", "orders@alpha", "gone"), cancellationToken: token);

        Assert.False(Assert.Single(reply.Outcomes).Ok);
        Assert.False(broker.Runtime.AttentionPending(Parley.Core.AgentName.Create("orders@alpha")));
    }
}
