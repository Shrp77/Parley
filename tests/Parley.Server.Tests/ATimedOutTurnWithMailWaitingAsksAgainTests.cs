using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a turn which ran out of time while mail was still uncollected asks for another turn (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// Found by review, lens 1 run 103. A node says nothing to an agent while a reply is still owed to a turn that gave up, so
/// such a turn ends timed out having delivered nothing, and spends the wake of the mail that arrived meanwhile. Nothing
/// asked again: the agent finished its old work and sat idle with that mail, flagged as never collected.
/// </remarks>
[Integration]
public sealed class ATimedOutTurnWithMailWaitingAsksAgainTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static AgentName Orders => AgentName.Create("orders@alpha");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    public async Task A_turn_that_ran_out_of_time_with_mail_uncollected_asks_again(bool timedOut, bool collected, int commands)
    {
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(Ct, clock: clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct, startedBy: node);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        // The mail, and the turn it asks for.
        _ = await billing.Client.RequestAsync(Requests.Open("billing@alpha", "orders@alpha", "net or gross?"), cancellationToken: Ct);
        _ = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        var started = clock.GetUtcNow();
        Assert.True(await broker.Runtime.TurnStartedAsync("alpha", Orders, started, Ct));

        // Work it collected is its own to finish, however long it takes: only mail still uncollected asks again.
        if (collected)
        {
            Assert.Single((await orders.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: Ct)).Messages);
        }

        // The turn runs out of time; the wake's claim has long expired.
        clock.Advance(TimeSpan.FromMinutes(21));

        Assert.True(await broker.Runtime.TurnEndedAsync(
            "alpha",
            Orders,
            new EndedTurn(started, TimeSpan.FromMinutes(20), Clean: !timedOut, TimedOut: timedOut, ProcessDied: false,
                PermissionDenials: 0, CostUsd: 0, ModelTurns: 0),
            Ct));

        // A turn that ended in time had its chance at the mail; one that ran out of time is given another.
        Assert.Equal(commands, await node.CountCommandsAsync(commands, Settle));
    }

    [Fact]
    public async Task A_turn_that_ran_out_of_time_with_only_a_vote_owed_asks_again()
    {
        // Lens 1 run 104: a closure poll waiting on the agent's vote wakes it once (SPEC.md §6) and is not a message, so
        // a turn that ran out of time spending that wake left the poll waiting for ever.
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var broker = await LoopbackBroker.StartAsync(Ct, clock: clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct, startedBy: node);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        _ = await billing.Client.RequestAsync(Requests.Open("billing@alpha", "orders@alpha", "net or gross?", "rounding"), cancellationToken: Ct);
        var request = Assert.Single((await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: Ct)).Messages);
        // Billing proposes closing while orders holds the work, so both are polled; orders then answers and does not
        // vote, so it owes only its vote, and is woken for it.
        var concluding = await billing.Client.ConcludeAsync(
            new Wire.ConcludeRequest { From = "billing@alpha", ConversationId = "rounding", Vote = Wire.Vote.Done },
            cancellationToken: Ct);
        Assert.True(concluding.Ok, concluding.Refusal.ToString());
        Assert.False(concluding.Concluded);

        Assert.True((await orders.Client.ResultAsync(
            Requests.Result("orders@alpha", request.Message.ExchangeId), cancellationToken: Ct)).Ok);

        var started = clock.GetUtcNow();
        Assert.True(await broker.Runtime.TurnStartedAsync("alpha", Orders, started, Ct));
        var before = await node.CountCommandsAsync(Settle);

        clock.Advance(TimeSpan.FromMinutes(21));

        Assert.True(await broker.Runtime.TurnEndedAsync(
            "alpha",
            Orders,
            new EndedTurn(started, TimeSpan.FromMinutes(20), Clean: false, TimedOut: true, ProcessDied: false,
                PermissionDenials: 0, CostUsd: 0, ModelTurns: 0),
            Ct));

        Assert.Equal(before + 1, await node.CountCommandsAsync(before + 1, Settle));
    }
}
