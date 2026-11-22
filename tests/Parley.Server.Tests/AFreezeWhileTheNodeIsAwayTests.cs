using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A freeze made while the node that started an agent is away, and that node joining again (SPEC.md §14: a freeze ends the
/// process the way <c>stop</c> does).
/// </summary>
/// <remarks>
/// Review, lens 1 on 6fde4ef3: the freeze found no node to ask (<c>no_node</c>), and the node's return asked for nothing, so
/// the process it started ran on, frozen, while every surface said "stopping once its turn ends" and the terminal's row,
/// which leaves Stop out for a frozen agent, offered no way to end it.
/// </remarks>
[Integration]
public sealed class AFreezeWhileTheNodeIsAwayTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_process_is_stopped_once_its_node_returns()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var first = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        _ = await first.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = "t-1" }, cancellationToken: Ct);

        await using var agent = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, launchToken: "t-1", processId: 5151);

        // The node's stream ends (the broker let a silent connection go, or the node is reconnecting); its process runs on.
        await first.DisposeAsync();
        await broker.WaitUntilNodeGoneAsync(Here, TimeSpan.FromSeconds(10));

        var frozen = await broker.ConnectAsOperator().FreezeAsync(new Wire.FreezeRequest { Agent = Agent }, cancellationToken: Ct);

        Assert.True(frozen.Changed);
        Assert.Equal("no_node", frozen.Process);

        // The node joins again and announces the process it still runs, as TurnReporter.ReannounceAsync does.
        await using var again = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        _ = await again.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = "t-1" }, cancellationToken: Ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline
            && !again.Commands.Any(c => c.CommandCase == Wire.NodeCommand.CommandOneofCase.Stop && c.Stop.Agent == Agent))
        {
            await Task.Delay(20, Ct);
        }

        var stop = Assert.Single(again.Commands, c => c.CommandCase == Wire.NodeCommand.CommandOneofCase.Stop);

        Assert.Equal(Agent, stop.Stop.Agent);
        Assert.False(stop.Stop.Restart);
    }
}
