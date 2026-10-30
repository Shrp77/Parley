using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a turn which ran out of time while mail was still uncollected asks for another turn (SPEC.md §11.6).
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
}
