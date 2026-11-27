using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the refresh an agent is due after its first ownership line was accepted automatically is not sent while a person
/// has it frozen, and is sent once the freeze is lifted (SPEC.md §14, §11.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A refresh ends the process and starts it again</b>, and nothing starts a frozen agent. The refresh does not go
/// through the one place a start is asked for, so it has a check of its own, which is what this holds.
/// </para>
/// <para>
/// <b>Not marked as sent while frozen.</b> A refresh is sent once and never again, so one recorded as sent under a freeze
/// would be lost: the pass after the unfreeze sending it is the evidence that it was not.
/// </para>
/// <para>
/// The pass is driven on the runtime, as the serve loop drives it; the node, the freeze and the agent are on the wire.
/// </para>
/// </remarks>
[Integration]
public sealed class ARefreshWaitsForAnUnfreezeTests
{
    private const string Here = "alpha";
    private const string Orders = "orders@alpha";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(400);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_frozen_agent_due_a_refresh_is_not_refreshed_until_it_is_unfrozen_and_then_once()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        await AcceptedAutomaticallyAsync(broker);

        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct);

        var console = broker.ConnectAsOperator();

        Assert.True((await console.FreezeAsync(new Wire.FreezeRequest { Agent = Orders }, cancellationToken: Ct)).Changed);

        Assert.Empty(await broker.Runtime.RefreshAsync(Ct));
        Assert.Equal(0, await node.CountCommandsAsync(Briefly));

        Assert.True((await console.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct)).Changed);

        // The unfreeze itself asks for nothing: the agent owes nothing and nothing is waiting for it.
        Assert.Equal(0, await node.CountCommandsAsync(Briefly));

        // Still due, so the freeze did not use it up.
        Assert.Equal(AgentName.Create(Orders), Assert.Single(await broker.Runtime.RefreshAsync(Ct)));
        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Refresh, command.CommandCase);
        Assert.Equal(Orders, command.Refresh.Agent);

        // And once.
        Assert.Empty(await broker.Runtime.RefreshAsync(Ct));
        Assert.Equal(1, await node.CountCommandsAsync(Briefly));
    }

    /// <summary>
    /// Parley asks the agent what it owns, the agent answers, and the answer is accepted automatically, so a refresh is due.
    /// The agent then goes away. No node has joined yet, so nothing was asked of one.
    /// </summary>
    private static async Task AcceptedAutomaticallyAsync(LoopbackBroker broker)
    {
        await using (var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
            Assert.Empty(await broker.Runtime.FacilitateAsync(Ct));

            var question = Assert.Single((await orders.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct)).Messages);

            var answered = await orders.Client.ResultAsync(
                Requests.Result(Orders, question.Message.ExchangeId), cancellationToken: Ct);

            Assert.True(answered.Ok, "the agent's answer to Parley was refused, so there is nothing to accept");

            var answer = Assert.Single(await broker.Runtime.FacilitateAsync(Ct));

            Assert.True(await broker.Runtime.AcceptAutomaticallyAsync(answer, Ct), "the answer was not accepted, so no refresh is due");
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);
    }
}
