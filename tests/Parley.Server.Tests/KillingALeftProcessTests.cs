using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// An agent whose node is gone, and a person ending its process (SPEC.md §10.2 <c>kill</c>; §11.1).
/// </summary>
/// <remarks>
/// A process a node started keeps the launch token it was given, and a broker that restarted forgot which tokens were
/// reported, so such a process read as open outside Parley: blamed on somebody who never opened it. A token no node holds
/// is what tells it from one opened in an editor, which has none. Ending it is the only thing a person can do, because
/// nothing holds it to stop at the end of a turn.
/// </remarks>
[Integration]
public sealed class KillingALeftProcessTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Wire.RegistryRow> RowAsync(LoopbackBroker broker) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct))
            .Rows.Single(row => row.Name == Agent);

    [Fact]
    public async Task A_token_no_node_holds_reads_as_left_running_and_not_as_somebodys_editor()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var left = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242);

        var row = await RowAsync(broker);

        Assert.True(row.Unsupervised);
        Assert.True(row.OpenOutside);
    }

    [Fact]
    public async Task One_with_no_token_is_open_outside_Parley_and_not_left_running()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var editor = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, processId: 4242);

        var row = await RowAsync(broker);

        Assert.False(row.Unsupervised);
        Assert.True(row.OpenOutside);
    }

    [Fact]
    public async Task One_the_node_reported_is_supervised()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, startedBy: node, processId: 4242);

        var row = await RowAsync(broker);

        Assert.False(row.Unsupervised);
        Assert.False(row.OpenOutside);
    }

    [Fact]
    public async Task Killing_one_tells_the_node_on_its_machine_which_process_and_nothing_else()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var left = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242);

        var reply = await broker.ConnectAsOperator().KillAsync(
            new Wire.StopRequest { Agent = Agent, Because = "stuck" }, cancellationToken: Ct);

        Assert.True(reply.Changed);

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Kill, command.CommandCase);
        Assert.Equal(Agent, command.Kill.Agent);
        Assert.Equal(4242, command.Kill.ProcessId);
        Assert.Equal("stuck", command.Kill.Because);
    }

    [Fact]
    public async Task A_process_opened_in_an_editor_is_never_killed_from_here()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var editor = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, processId: 4242);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().KillAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("somebody's editor", refused.Status.Detail, StringComparison.Ordinal);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task A_supervised_process_is_stopped_and_not_killed()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, startedBy: node, processId: 4242);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().KillAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task An_adapter_that_named_no_process_leaves_nothing_to_end_and_says_so()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var left = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone");

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().KillAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("did not say which process", refused.Status.Detail, StringComparison.Ordinal);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task With_no_node_on_its_machine_it_says_what_to_do()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var left = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().KillAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("parley up", refused.Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("human-user@test")]
    [InlineData("parley-facilitator@test")]
    public async Task Neither_the_person_nor_Parley_is_killed(string who)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().KillAsync(new Wire.StopRequest { Agent = who }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
    }
}
