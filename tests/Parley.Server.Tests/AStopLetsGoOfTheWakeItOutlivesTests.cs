using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That stopping an agent lets go of a wake the stopped process was owed, so the next work for it starts it again
/// (SPEC.md §10.2 <c>stop</c>, §14).
/// </summary>
/// <remarks>
/// Work asks a running agent for a turn and marks the wake pending until the agent collects its mail. A process stopped
/// first never collects it, so a mark left behind suppressed every wake for two minutes and the work sat with no process
/// to take it. The stop's own reply says the next work starts the agent; this holds that it does.
/// </remarks>
[Integration]
public sealed class AStopLetsGoOfTheWakeItOutlivesTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Work_for_a_stopped_agent_asks_the_node_to_start_it()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = "t-1" }, cancellationToken: Ct);

        var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@m1", cancellationToken: Ct);

        // Work for the running agent: a turn is asked for, and the wake is marked pending until it collects.
        _ = await orders.Client.RequestAsync(Requests.Open("orders@m1", Agent, "first"), cancellationToken: Ct);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(300)));

        // The person stops it before it has collected anything, and the node ends the process.
        var reply = await broker.ConnectAsOperator().StopAsync(
            new Wire.StopRequest { Agent = Agent, Because = "stuck" }, cancellationToken: Ct);

        Assert.True(reply.Changed);
        Assert.Equal(2, await node.CountCommandsAsync(2, TimeSpan.FromMilliseconds(300)));

        await agent.DisposeAsync();

        _ = await orders.Client.RequestAsync(Requests.Open("orders@m1", Agent, "second"), cancellationToken: Ct);

        // A start, not silence.
        Assert.Equal(3, await node.CountCommandsAsync(3, TimeSpan.FromMilliseconds(300)));
    }
}
