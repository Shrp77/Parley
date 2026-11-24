using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A node's word on how many background tasks an agent has running (SPEC.md §11.6), as the registry carries it.
/// </summary>
/// <remarks>
/// Held in memory with the node that said it, like a running turn: zero clears it, the node leaving clears it, and a new
/// process launched under the name clears it. Only the node joined for the agent's machine is heard.
/// </remarks>
[Integration]
public sealed class BackgroundWorkTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> CountAsync(LoopbackBroker broker) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
        .Single(row => row.Name == Agent).BackgroundTasks;

    private static async Task LaunchAsync(JoinedNodeClient node, string token) =>
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = token }, cancellationToken: Ct);

    private static async Task ReportAsync(JoinedNodeClient node, int tasks, string machine = Here, string agent = Agent) =>
        _ = await node.Nodes.ReportTurnAsync(
            new Wire.TurnReport { Machine = machine, Agent = agent, Background = new Wire.BackgroundWork { Tasks = tasks } },
            cancellationToken: Ct);

    [Fact]
    public async Task What_the_node_says_is_on_the_registry_row_until_it_says_none()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await LaunchAsync(node, "t-1");
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");

        Assert.Equal(0, await CountAsync(broker));

        await ReportAsync(node, 2);
        Assert.Equal(2, await CountAsync(broker));

        await ReportAsync(node, 0);
        Assert.Equal(0, await CountAsync(broker));
    }

    [Fact]
    public async Task A_new_process_under_the_name_starts_with_none_and_the_same_one_again_keeps_its_count()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await LaunchAsync(node, "t-1");
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, 3);

        // The node telling a broker again about the process it already runs.
        await LaunchAsync(node, "t-1");
        Assert.Equal(3, await CountAsync(broker));

        // A restart: a new process, which has started nothing yet.
        await LaunchAsync(node, "t-2");
        await using var again = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-2");

        Assert.Equal(0, await CountAsync(broker));
    }

    [Fact]
    public async Task A_node_that_leaves_takes_its_word_with_it()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await LaunchAsync(node, "t-1");
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, 1);
        Assert.Equal(1, await CountAsync(broker));

        await node.DisposeAsync();

        // The node comes back, as after a dropped connection, and has said nothing about background work since. With no
        // node joined the count reads zero whatever is held, so it is read only once one is joined again.
        JoinedNodeClient? back = null;

        for (var tries = 0; tries < 50 && back is null; tries++)
        {
            try
            {
                back = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
            }
            catch (Grpc.Core.RpcException refused) when (refused.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
            {
                await Task.Delay(100, Ct);
            }
        }

        Assert.NotNull(back);
        await using var rejoined = back;

        Assert.Equal(0, await CountAsync(broker));
    }

    [Theory]
    [InlineData("m2")]
    [InlineData(Here)]
    public async Task Only_the_node_joined_for_the_agents_machine_is_heard(string reportedFrom)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var other = await JoinedNodeClient.JoinAsync(broker, "m2", ["billing"], Ct, reportsTurns: true);
        await LaunchAsync(node, "t-1");
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(reportedFrom == Here ? node : other, 4, machine: reportedFrom);

        Assert.Equal(reportedFrom == Here ? 4 : 0, await CountAsync(broker));
    }

    [Fact]
    public async Task One_open_outside_Parley_shows_none_whatever_was_said()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);

        await ReportAsync(node, 2);

        Assert.Equal(0, await CountAsync(broker));
    }
}
