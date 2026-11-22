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
    public async Task Work_that_arrives_while_a_stopped_agent_is_still_attached_starts_it_once_its_process_has_gone()
    {
        // While the stopped process is still attached, new work for it is asked for as a turn. Its node gives that turn to a
        // process the stop then ends, so it was lost: nothing started the agent, and the next message was told a turn was
        // already coming (review, lens 1 on 0.64.6). "The next work for it starts it again" (SPEC.md §10.2 stop).
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            _ = await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            var sent = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "after the stop", "c1"), cancellationToken: Ct);

            Assert.Equal(Wire.WakeOutcome.AlreadyRunning, Assert.Single(sent.Outcomes).Wake);
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);
        }

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Agent, command.Start.Agent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Work_that_arrived_for_a_stopped_agent_does_not_start_it_once_a_person_has_retired_or_purged_it(bool purge)
    {
        // Retirement is a person's judgement. Work that arrived before it must not start the agent after it: the person's own
        // Start is refused for a retired agent, and a peer's mail must not do what that refuses (review, lens 2 on 0.64.7).
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        var operators = broker.ConnectAsOperator();

        await using (var agent = await LaunchedAsync(broker, node))
        {
            _ = await operators.StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);
            _ = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

            _ = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "after the stop", "c1"), cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            _ = await operators.RetireAsync(new Wire.AgentRequest { Agent = Agent }, cancellationToken: Ct);

            if (purge)
            {
                _ = await operators.PurgeAsync(new Wire.AgentRequest { Agent = Agent }, cancellationToken: Ct);
            }
        }

        await broker.WaitUntilDetachedAsync(Agent, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task Work_the_stopping_process_read_before_it_went_does_not_start_the_agent_again()
    {
        // The start is for work still waiting. What the stopping process collected is read, and a spare start for it would
        // undo the person's stop for nothing.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            _ = await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);
            _ = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

            _ = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "after the stop", "c1"), cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            Assert.Single((await agent.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Agent }, cancellationToken: Ct)).Messages);
        }

        await broker.WaitUntilDetachedAsync(Agent, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task A_stopped_agent_nothing_arrived_for_is_not_started_when_its_process_goes()
    {
        // A stop is a stop: only work that arrived meanwhile starts it again, never the work it already owed.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            _ = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "before the stop", "c1"), cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            _ = await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);
        }

        await broker.WaitUntilDetachedAsync(Agent, TimeSpan.FromSeconds(5));
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
