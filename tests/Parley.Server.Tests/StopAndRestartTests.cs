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
    public async Task A_vote_asked_of_a_stopped_agent_while_it_is_still_attached_starts_it_once_its_process_has_gone()
    {
        // A closure poll is work too, and it is no mail: the agent has collected everything sent to it, so the poll is the
        // only thing still waiting when its process goes. The turn asked for the vote went to the process the stop ended.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            var sent = Assert.Single((await docs.Client.RequestAsync(
                Requests.Open("docs@m1", Agent, "before the stop", "c1"), cancellationToken: Ct)).Outcomes);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);
            Assert.Single((await agent.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Agent }, cancellationToken: Ct)).Messages);

            _ = await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            // Proposed after the stop, while the agent holds the work, so the agent is polled; nobody is asked for a vote
            // while it still owes the answer.
            var proposed = await docs.Client.ConcludeAsync(
                new Wire.ConcludeRequest { From = "docs@m1", ConversationId = "c1", Vote = Wire.Vote.Done }, cancellationToken: Ct);

            Assert.True(proposed.Ok);
            Assert.False(proposed.Concluded, "the conversation closed, so no poll waits on anybody");
            await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));

            // Its answer leaves the conversation waiting on nothing but its vote, and a turn is asked for that.
            Assert.True((await agent.Client.ResultAsync(Requests.Result(Agent, sent.ExchangeId), cancellationToken: Ct)).Ok);
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            // Nothing sent to it is uncollected: the poll alone waits.
            Assert.Empty((await agent.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Agent }, cancellationToken: Ct)).Messages);
        }

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Agent, command.Start.Agent);
    }

    [Fact]
    public async Task A_persons_start_of_an_agent_that_is_still_stopping_is_made_once_its_process_has_gone()
    {
        // The agent is still attached, so the start goes out as a turn, which the stop takes with it. The person was told its
        // node had been asked to start it, and with nothing waiting for the agent nothing did (review, lens 1 on 0.64.10).
        // A turn asked of a process that ends before taking it is not lost, a person's Start included (SPEC.md §11.1).
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        var operators = broker.ConnectAsOperator();

        await using (var agent = await LaunchedAsync(broker, node))
        {
            _ = await operators.StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            Assert.True((await operators.StartAsync(new Wire.StartRequest { Agent = Agent }, cancellationToken: Ct)).Changed);
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);
        }

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Agent, command.Start.Agent);
    }

    [Fact]
    public async Task A_stop_forgets_a_persons_start_asked_before_it()
    {
        // Start, then Stop before the turn begins: the stop is the person's later word. Left remembered, the start was made
        // when the process went, and the agent the person had just stopped came back with nothing waiting for it.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        var operators = broker.ConnectAsOperator();

        await using (var agent = await LaunchedAsync(broker, node))
        {
            Assert.True((await operators.StartAsync(new Wire.StartRequest { Agent = Agent }, cancellationToken: Ct)).Changed);
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            _ = await operators.StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);
        }

        await broker.WaitUntilDetachedAsync(Agent, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task A_stop_lets_go_of_the_claim_an_earlier_message_left_so_the_next_message_is_asked_for()
    {
        // An uncollected message's wake holds a claim for two minutes. Kept through a stop, the claim answered the next
        // message "already coming": no turn was asked or noted for it, nothing started the agent when it went, and the mail sat.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            _ = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "before the stop", "c1"), cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            _ = await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            var after = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "after the stop", "c2"), cancellationToken: Ct);

            Assert.Equal(Wire.WakeOutcome.AlreadyRunning, Assert.Single(after.Outcomes).Wake);
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);
        }

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Agent, command.Start.Agent);
    }

    [Fact]
    public async Task A_persons_start_that_was_answered_with_a_turn_is_not_owed_again_when_the_process_goes_later()
    {
        // The node reports the turn begun, which is the asking answered. Without that the note would wait for the process to
        // end, perhaps days on, and start the agent then for a request long since met.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            Assert.True((await broker.ConnectAsOperator().StartAsync(new Wire.StartRequest { Agent = Agent }, cancellationToken: Ct)).Changed);
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);

            Assert.True(await broker.Runtime.TurnStartedAsync(Here, Parley.Core.AgentName.Create(Agent), DateTimeOffset.UtcNow, Ct));
        }

        await broker.WaitUntilDetachedAsync(Agent, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task A_turn_asked_of_a_process_that_then_goes_with_nobody_stopping_it_here_is_asked_for_again_as_a_start()
    {
        // One checkout can serve two projects, and a stop asked through the other project's broker is one this broker never
        // hears of. The turn it asked for a message here went to the process that stop ended, the sender was told the agent
        // was running, and the next sender was told a turn was already coming (review, lens 1 on 0.64.10). The same holds
        // for a process that simply dies.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            var sent = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "no stop was asked here", "c1"), cancellationToken: Ct);

            Assert.Equal(Wire.WakeOutcome.AlreadyRunning, Assert.Single(sent.Outcomes).Wake);
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(TimeSpan.FromSeconds(5))).CommandCase);
        }

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Agent, command.Start.Agent);
    }

    [Fact]
    public async Task A_turn_that_was_taken_leaves_nothing_to_start_for_when_the_process_goes()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        await using (var agent = await LaunchedAsync(broker, node))
        {
            _ = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "read in its turn", "c1"), cancellationToken: Ct);

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
    public async Task An_agent_whose_node_has_gone_is_refused_with_what_to_do()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await LaunchedAsync(broker, node);

        // The node goes, as it did when a dropped stream ended a hosted node; the agent's own connection stays. Its token
        // went with the node, so the process is left running and only a kill ends it (SPEC.md §11.1).
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
        Assert.Contains("left by a node that is gone", refused.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("kill", refused.Status.Detail, StringComparison.Ordinal);
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
