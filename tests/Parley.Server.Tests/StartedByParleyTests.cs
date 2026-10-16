using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the broker tells a process its node started from one opened outside Parley, and acts on the difference
/// (SPEC.md §11.7).
/// </summary>
/// <remarks>
/// What an agent started outside Parley does, decided by a launch token rather than by asking the model. An agent opened
/// in an editor must not read as running, a wake on it must not ask the node for a turn it could not give and mark one
/// as coming, and its turn standing must not borrow whatever the node last said of that name.
/// </remarks>
public sealed class StartedByParleyTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Wire.RegistryRow> RowAsync(LoopbackBroker broker) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
        .Single(row => row.Name == Agent);

    private static async Task LaunchAsync(JoinedNodeClient node, string token, string machine = Here) =>
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = machine, Agent = Agent, Token = token }, cancellationToken: Ct);

    [Fact]
    public async Task A_process_presenting_its_nodes_token_is_Parleys_and_its_turns_are_reported()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await LaunchAsync(node, "t-1");
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");

        var row = await RowAsync(broker);

        Assert.False(row.OpenOutside);
        Assert.True(row.TurnsReported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-token")]
    public async Task Anything_else_is_open_outside_Parley_and_its_turns_are_unknown(string? presented)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await LaunchAsync(node, "t-1");
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: presented);

        var row = await RowAsync(broker);

        Assert.True(row.OpenOutside);
        Assert.False(row.TurnsReported);
    }

    [Fact]
    public async Task A_token_is_spent_by_the_first_process_to_present_it()
    {
        // An editor started from a shell that inherited the variable must not pass for the process the node started.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await LaunchAsync(node, "t-1");
        var first = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");
        await first.DisposeAsync();

        await using var second = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");

        Assert.True((await RowAsync(broker)).OpenOutside);
    }

    [Fact]
    public async Task A_launch_reported_by_another_machines_node_names_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var elsewhere = await JoinedNodeClient.JoinAsync(broker, "m2", [], Ct);

        await LaunchAsync(elsewhere, "t-1", machine: "m2");
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");

        Assert.True((await RowAsync(broker)).OpenOutside);
    }

    [Fact]
    public async Task A_request_to_an_agent_open_only_outside_Parley_asks_nobody_and_says_so()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@m1", cancellationToken: Ct);

        var outcome = Assert.Single((await orders.Client.RequestAsync(
            Requests.Open("orders@m1", Agent, "can you look at this?"), cancellationToken: Ct)).Outcomes);

        Assert.Equal(Wire.WakeOutcome.OpenOutside, outcome.Wake);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Start_refuses_an_agent_open_outside_Parley()
    {
        // A second process in one checkout is two models editing it.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().StartAsync(new Wire.StartRequest { Agent = Agent }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("open outside Parley", refused.Status.Detail, StringComparison.Ordinal);
    }
}
