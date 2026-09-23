using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The wake and its bound with everything happening at once.
/// </summary>
/// <remarks>
/// The earlier suites hold one variable still each. This holds none: several senders, several
/// recipients, drains landing in the middle, all over a real socket. What is asserted is the pair of
/// invariants the feature rests on — no message is lost, and no recipient is woken more than its own
/// reading justifies.
/// </remarks>
public sealed class WakeUnderLoadTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    private static async Task RegisterAndCloseAsync(
        LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    [Fact]
    public async Task Five_recipients_flooded_at_once_cost_five_turns_and_lose_nothing()
    {
        // Twenty notices apiece from four senders. The bound is per recipient, so the ceiling on
        // node commands is the number of recipients -- not the number of notices, and not the
        // number of pairs.
        var token = TestContext.Current.CancellationToken;

        string[] recipients = ["orders", "billing", "inventory", "docs", "infra"];

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", recipients, token);

        foreach (var role in recipients)
        {
            await RegisterAndCloseAsync(broker, $"{role}@alpha", token);
        }

        var senders = new List<AttachedClient>();

        try
        {
            for (var at = 0; at < 4; at++)
            {
                senders.Add(await AttachedClient.AttachAsync(
                    broker, $"sender{at}@alpha", cancellationToken: token));
            }

            using var start = new Barrier(senders.Count);

            await Task.WhenAll(senders.Select((sender, which) => Task.Run(
                async () =>
                {
                    start.SignalAndWait(token);

                    for (var round = 0; round < 20; round++)
                    {
                        var to = recipients[round % recipients.Length];

                        var reply = await sender.Client.RequestAsync(
                            Requests.Open($"sender{which}@alpha", $"{to}@alpha", $"s{which} r{round}"), cancellationToken: token);

                        Assert.True(Assert.Single(reply.Outcomes).Ok, $"a request from sender{which} was refused");
                    }
                },
                token)));

            // At least one is owed — the assertion below says so — so it is waited for rather than
            // slept over, and the settling window then catches any extras the ceiling should have
            // suppressed. A fixed sleep alone could not tell "woke nobody" from "not yet".
            var commands = await node.CountCommandsAsync(1, Settle);

            Assert.True(
                commands <= recipients.Length,
                $"80 notices to 5 recipients bought {commands} turns");

            Assert.True(commands > 0, "80 notices woke nobody at all");
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
    public async Task Drains_landing_mid_flood_lose_no_message()
    {
        // **The invariant that would be silent if it broke**, and the assertion is worth reading
        // carefully because the first version of it was wrong. `DrainInbox` returns what it
        // *collected*, not what remains — so "the last drain returned one message" is the safe case
        // and this test read it as the failing one, and sent me to reorder code that was correct.
        //
        // What actually has to hold is that every notice sent is collected by some drain. Counted
        // across all of them, which is the only way to see a message that fell between two.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", cancellationToken: token);
        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var collected = 0;

        for (var round = 0; round < 60; round++)
        {
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(
                    async () =>
                    {
                        start.SignalAndWait(token);

                        _ = await sender.Client.RequestAsync(
                            Requests.Open("billing@alpha", "orders@alpha", $"round {round}"), cancellationToken: token);
                    },
                    token),
                Task.Run(
                    async () =>
                    {
                        start.SignalAndWait(token);

                        var taken = await orders.Client.DrainInboxAsync(
                            new Wire.DrainInboxRequest { Agent = "orders@alpha" },
                            cancellationToken: token);

                        Interlocked.Add(ref collected, taken.Messages.Count);
                    },
                    token));
        }

        // Everything the interleaving left behind, taken now.
        var last = await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: token);

        collected += last.Messages.Count;

        Assert.Equal(60, collected);

        // And nothing at all remains: a second drain that still returns something would mean the
        // first one had not in fact taken everything, which is a different defect from a lost
        // message and would look identical from outside.
        var after = await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: token);

        Assert.Empty(after.Messages);
    }

    [Fact]
    public async Task Housekeeping_running_alongside_the_bound_disturbs_neither()
    {
        // The broker's own tick closes satisfied polls, sweeps instructions and prunes on a timer, and it takes the same gate
        // every operation takes. A mark held across one must survive it, and the tick must not
        // throw because a mark exists.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var name = AgentName.Create("orders@alpha");

        Assert.Equal(WakeOutcome.Started, broker.Runtime.RequestAttentionForMail(name, "told"));

        _ = await broker.Runtime.TickAsync(token);

        Assert.True(
            broker.Runtime.AttentionPending(name),
            "housekeeping cleared a pending wake it knows nothing about");

        Assert.Equal(
            WakeOutcome.AlreadyComing, broker.Runtime.RequestAttentionForMail(name, "told again"));
    }
}
