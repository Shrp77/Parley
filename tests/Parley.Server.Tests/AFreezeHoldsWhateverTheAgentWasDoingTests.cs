using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a freeze holds an agent that is running, starting or waiting on an answer, and that unfreezing it pays what the
/// freeze put off (SPEC.md §14).
/// </summary>
/// <remarks>
/// <c>FreezingAnAgentTests</c> freezes an agent that is at rest. Each case here was found by review of 0.64.3 with the agent
/// in motion: a start already asked for, a process still up, an answer on its way. In each the registry said frozen while
/// something else was true.
/// </remarks>
[Integration]
public sealed class AFreezeHoldsWhateverTheAgentWasDoingTests
{
    private const string Here = "alpha";
    private const string Orders = "orders@alpha";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(400);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.FreezeRequest Freeze(string name) => new() { Agent = name };

    private static async Task RegisterAndCloseAsync(LoopbackBroker broker, string name)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: Ct))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    private static async Task<AttachedClient> LaunchedAsync(LoopbackBroker broker, JoinedNodeClient node, string token)
    {
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Orders, Token = token }, cancellationToken: Ct);

        return await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct, launchToken: token);
    }

    [Fact]
    public async Task Freezing_an_agent_its_node_is_running_asks_that_node_to_stop_it_and_not_to_start_it_again()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var agent = await LaunchedAsync(broker, node, "t-1");

        var frozen = await broker.ConnectAsOperator().FreezeAsync(Freeze(Orders), cancellationToken: Ct);

        Assert.Equal("stopping", frozen.Process);

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, command.CommandCase);
        Assert.Equal(Orders, command.Stop.Agent);
        Assert.False(command.Stop.Restart);
    }

    [Fact]
    public async Task An_agent_frozen_while_it_was_starting_is_stopped_when_it_comes_up()
    {
        // The freeze found no process to stop, because the start the node had been asked for had not attached yet. The agent
        // then came up and worked while every surface said frozen, and no stop was ever asked for.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);

        await RegisterAndCloseAsync(broker, Orders);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: Ct);

        var sent = await docs.Client.RequestAsync(Requests.Open("docs@alpha", Orders, "start for this", "c1"), cancellationToken: Ct);

        Assert.Equal(Wire.WakeOutcome.Started, Assert.Single(sent.Outcomes).Wake);
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, (await node.WaitForCommandAsync(Patience)).CommandCase);

        var frozen = await broker.ConnectAsOperator().FreezeAsync(Freeze(Orders), cancellationToken: Ct);

        Assert.True(frozen.Changed);
        Assert.Equal("not_running", frozen.Process);

        await using var agent = await LaunchedAsync(broker, node, "t-2");

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, command.CommandCase);
        Assert.Equal(Orders, command.Stop.Agent);
        Assert.False(command.Stop.Restart);
    }

    [Fact]
    public async Task An_agent_somebody_opened_in_an_editor_while_frozen_is_not_stopped()
    {
        // Only a process a node started is Parley's to stop (SPEC.md §11.7).
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);

        await RegisterAndCloseAsync(broker, Orders);
        _ = await broker.ConnectAsOperator().FreezeAsync(Freeze(Orders), cancellationToken: Ct);

        await using var agent = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task A_restart_of_a_frozen_agent_is_refused_in_words_because_it_would_start_it_again()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var agent = await LaunchedAsync(broker, node, "t-1");

        var operators = broker.ConnectAsOperator();
        _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(Patience)).CommandCase);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await operators.RestartAsync(new Wire.StopRequest { Agent = Orders }, cancellationToken: Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("Unfreeze it first", refused.Status.Detail, StringComparison.Ordinal);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task An_answer_that_arrived_while_its_asker_was_frozen_gets_a_turn_when_the_asker_is_unfrozen()
    {
        // The answer's wake was declined as frozen and its sender was told nothing is lost. The asker owes nothing on its own
        // question, so an unfreeze that asked only about work owed left the answer unread until something unrelated arrived.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        long exchange;

        await using (var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
            var asked = Assert.Single((await orders.Client.RequestAsync(
                Requests.Open(Orders, "billing@alpha", "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

            Assert.True(asked.Ok, asked.Rejection.ToString());
            exchange = asked.ExchangeId;
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);

        var operators = broker.ConnectAsOperator();
        _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);

        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: Ct);
        _ = await billing.Client.ResultAsync(Requests.Result("billing@alpha", exchange), cancellationToken: Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));

        Assert.True((await operators.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct)).Changed);

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);
    }

    [Fact]
    public async Task An_unfreeze_before_the_frozen_agents_turn_has_ended_is_paid_when_its_process_goes()
    {
        // The freeze's stop waits for the turn. Unfrozen before the turn ends, the agent is still attached, so the unfreeze's
        // wake is a turn, which its node gives to a process the stop then ends. Nothing asked again: the agent was left unfrozen
        // and stopped with its answer unread, and the next message was told a turn was already coming (review, lens 1 on 0.64.4).
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        var operators = broker.ConnectAsOperator();

        await using (var orders = await LaunchedAsync(broker, node, "t-1"))
        {
            var asked = Assert.Single((await orders.Client.RequestAsync(
                Requests.Open(Orders, "billing@alpha", "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

            _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(Patience)).CommandCase);

            _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: Ct);
            _ = await billing.Client.ResultAsync(Requests.Result("billing@alpha", asked.ExchangeId), cancellationToken: Ct);

            Assert.True((await operators.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct)).Changed);

            // Still attached, so what the unfreeze asks for is a turn, of the process the stop is about to end.
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(Patience)).CommandCase);
        }

        // The stop has taken the process. Its answer is still unread, so it is started for it.
        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);
    }

    [Fact]
    public async Task With_a_second_session_open_what_the_unfreeze_owes_is_asked_for_when_the_last_one_goes_and_not_before()
    {
        // Two sessions for one agent are legitimate: the node's process and somebody's editor. The editor leaving first must
        // not spend what the unfreeze owes, because the process the stop is ending is still there to take the turn and lose it.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        var operators = broker.ConnectAsOperator();
        var launched = await LaunchedAsync(broker, node, "t-1");
        var editor = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var editorOpen = true;

        try
        {
            var asked = Assert.Single((await launched.Client.RequestAsync(
                Requests.Open(Orders, "billing@alpha", "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

            _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(Patience)).CommandCase);

            _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: Ct);
            _ = await billing.Client.ResultAsync(Requests.Result("billing@alpha", asked.ExchangeId), cancellationToken: Ct);

            _ = await operators.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(Patience)).CommandCase);

            await editor.DisposeAsync();
            editorOpen = false;

            await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
        }
        finally
        {
            if (editorOpen)
            {
                await editor.DisposeAsync();
            }

            await launched.DisposeAsync();
        }

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);
    }

    [Fact]
    public async Task An_agent_open_only_in_an_editor_when_it_was_unfrozen_is_not_started_when_the_editor_closes()
    {
        // Nothing was stopping it, so the turn the unfreeze could not give is not a start owed later: the next message starts it.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        var operators = broker.ConnectAsOperator();

        await using (var editor = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
            var asked = Assert.Single((await editor.Client.RequestAsync(
                Requests.Open(Orders, "billing@alpha", "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

            _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);

            _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: Ct);
            _ = await billing.Client.ResultAsync(Requests.Result("billing@alpha", asked.ExchangeId), cancellationToken: Ct);

            _ = await operators.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task An_agent_frozen_again_before_its_process_went_is_not_started_when_it_goes()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        var operators = broker.ConnectAsOperator();

        await using (var orders = await LaunchedAsync(broker, node, "t-1"))
        {
            var asked = Assert.Single((await orders.Client.RequestAsync(
                Requests.Open(Orders, "billing@alpha", "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

            _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);
            _ = await node.WaitForCommandAsync(Patience);

            _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: Ct);
            _ = await billing.Client.ResultAsync(Requests.Result("billing@alpha", asked.ExchangeId), cancellationToken: Ct);

            _ = await operators.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);
            _ = await node.WaitForCommandAsync(Patience);
            _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(Patience)).CommandCase);
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task Unfreezing_a_retired_agent_starts_nothing_whatever_is_waiting_for_it()
    {
        // Retirement is a person's judgement, and a freeze ending is not evidence against it.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: Ct);

        long exchange;

        await using (var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
            exchange = Assert.Single((await orders.Client.RequestAsync(
                Requests.Open(Orders, "billing@alpha", "which ledger?", "c1"), cancellationToken: Ct)).Outcomes).ExchangeId;
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);

        var operators = broker.ConnectAsOperator();
        _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);

        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: Ct);
        _ = await billing.Client.ResultAsync(Requests.Result("billing@alpha", exchange), cancellationToken: Ct);

        _ = await operators.RetireAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);
        _ = await operators.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task Unfreezing_an_agent_with_nothing_owed_and_nothing_waiting_starts_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);

        await RegisterAndCloseAsync(broker, Orders);

        var operators = broker.ConnectAsOperator();
        _ = await operators.FreezeAsync(Freeze(Orders), cancellationToken: Ct);
        _ = await operators.UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }
}
