using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A person stopping or restarting an agent (SPEC.md §10.2 <c>stop</c>, <c>restart</c>; §11.1, the fourth way an agent
/// ends).
/// </summary>
/// <remarks>
/// The broker only asks: the node waits for the turn and does the stopping, so what is held here is who is asked, with
/// what, and what is refused before anything is asked.
/// </remarks>
[Integration]
public sealed class StopAndRestartTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<AttachedClient> LaunchedAsync(LoopbackBroker broker, JoinedNodeClient node)
    {
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = "t-1" }, cancellationToken: Ct);

        return await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_agent_its_node_started_is_stopped_through_that_node(bool restart)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await LaunchedAsync(broker, node);
        var operators = broker.ConnectAsOperator();
        var request = new Wire.StopRequest { Agent = Agent, Because = "stuck" };

        var reply = restart
            ? await operators.RestartAsync(request, cancellationToken: Ct)
            : await operators.StopAsync(request, cancellationToken: Ct);

        Assert.True(reply.Changed);

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, command.CommandCase);
        Assert.Equal(Agent, command.Stop.Agent);
        Assert.Equal(restart, command.Stop.Restart);
        Assert.Equal("stuck", command.Stop.Because);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_agent_that_is_not_running_is_not_asked_about_and_nothing_is_started(bool restart)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        var operators = broker.ConnectAsOperator();
        var request = new Wire.StopRequest { Agent = Agent };

        var reply = restart
            ? await operators.RestartAsync(request, cancellationToken: Ct)
            : await operators.StopAsync(request, cancellationToken: Ct);

        Assert.False(reply.Changed);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task An_agent_open_outside_Parley_is_refused_and_its_node_is_not_asked()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("open outside Parley", refused.Status.Detail, StringComparison.Ordinal);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task An_agent_whose_machine_has_no_node_is_refused_with_what_to_do()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await LaunchedAsync(broker, node);

        // The node goes, as it did when a dropped stream ended a hosted node; the agent's own connection stays.
        await node.DisposeAsync();

        RpcException? refused = null;

        for (var waited = 0; waited < 50 && refused is null; waited++)
        {
            try
            {
                _ = await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);
                await Task.Delay(100, Ct);
            }
            catch (RpcException exception)
            {
                refused = exception;
            }
        }

        Assert.NotNull(refused);
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("parley up", refused.Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("human-user@test")]
    [InlineData("parley-facilitator@test")]
    public async Task Neither_the_person_nor_Parley_is_stopped(string who)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = who }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
    }
}
