using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the broker retires an agent its node found with no workspace, and only on that node's word (SPEC.md §14).
/// </summary>
/// <remarks>
/// SPEC promised it and nothing did it: a missing workspace was one more failed start in the node's output, and the
/// registry went on describing a repository that was not there, its node trying to start it whenever it was asked for.
/// The node looks, because the broker never reads an agent's disk.
/// </remarks>
public sealed class AWorkspaceGoneRetiresItsAgentTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Wire.AgentStatus> StatusAsync(LoopbackBroker broker, string agent = Agent) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
        .Single(row => row.Name == agent).Status;

    private static async Task RegisterAsync(LoopbackBroker broker, string agent)
    {
        await using (await AttachedClient.AttachAsync(broker, agent, cancellationToken: Ct))
        {
        }

        await broker.WaitUntilDetachedAsync(agent, TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> ReportAsync(JoinedNodeClient node, string agent, string machine = Here) =>
        (await node.Nodes.ReportWorkspaceGoneAsync(
            new Wire.WorkspaceGoneReport { Machine = machine, Agent = agent }, cancellationToken: Ct)).Retired;

    [Fact]
    public async Task The_node_for_the_agents_machine_retires_it_once()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await RegisterAsync(broker, Agent);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct);

        Assert.True(await ReportAsync(node, Agent));
        Assert.Equal(Wire.AgentStatus.Retired, await StatusAsync(broker));

        // Already retired: nothing changes, and the reply says so.
        Assert.False(await ReportAsync(node, Agent));
    }

    [Fact]
    public async Task Another_machines_node_retires_nobody()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await RegisterAsync(broker, Agent);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "m2", ["billing"], Ct);

        Assert.False(await ReportAsync(node, Agent, machine: "m2"));
        Assert.False(await ReportAsync(node, Agent, machine: Here));
        Assert.Equal(Wire.AgentStatus.Active, await StatusAsync(broker));
    }

    [Fact]
    public async Task A_report_for_a_machine_with_no_node_joined_retires_nobody()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await RegisterAsync(broker, Agent);

        var nodes = broker.ConnectAsNode();
        var reply = await nodes.ReportWorkspaceGoneAsync(
            new Wire.WorkspaceGoneReport { Machine = Here, Agent = Agent }, cancellationToken: Ct);

        Assert.False(reply.Retired);
        Assert.Equal(Wire.AgentStatus.Active, await StatusAsync(broker));
    }
}
