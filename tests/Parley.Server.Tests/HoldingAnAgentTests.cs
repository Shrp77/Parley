using Grpc.Core;
using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a person's hold on an agent keeps it from being started by anything, and that resuming it undoes only that (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is held is the choke point, not each caller.</b> The restart wake, new work and a person's Start all come to
/// one method, so these tests drive each of them and expect the node to hear nothing. A test of one would pass if a
/// second path were left open.
/// </para>
/// <para>
/// <b>Nothing is lost.</b> A hold closes, moves and answers nothing, so every test that holds an agent with work waiting
/// checks the work is still there afterwards.
/// </para>
/// </remarks>
[Integration]
public sealed class HoldingAnAgentTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static async Task RegisterAndCloseAsync(LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    private static Wire.AgentRequest Agent(string name) => new() { Agent = name };

    [Fact]
    public async Task Work_for_a_held_agent_does_not_start_it_and_the_sender_is_told_why()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var held = await broker.ConnectAsOperator().HoldAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.True(held.Changed);
        Assert.Equal("not_running", held.Process);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);

        var sent = await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "orders@alpha", "while you are held", "c1"), cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.Held, Assert.Single(sent.Outcomes).Wake);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_persons_start_of_a_held_agent_is_refused_in_words()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();
        _ = await console.HoldAsync(Agent("orders@alpha"), cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.StartAsync(new Wire.StartRequest { Agent = "orders@alpha" }, cancellationToken: token));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("Resume it first", refused.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Holding_twice_says_it_changed_nothing_and_resuming_says_whether_it_was_held()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();

        Assert.True((await console.HoldAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);
        Assert.False((await console.HoldAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);
        Assert.True((await console.ResumeAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);
        Assert.False((await console.ResumeAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);
    }

    [Fact]
    public async Task Nothing_unregistered_and_neither_participant_the_broker_provides_can_be_held()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        var console = broker.ConnectAsOperator();

        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.HoldAsync(Agent("ghost@alpha"), cancellationToken: token));

        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);

        var seat = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.HoldAsync(Agent($"human-user@{DeadProject}"), cancellationToken: token));

        Assert.Equal(StatusCode.FailedPrecondition, seat.StatusCode);

        var facilitator = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.HoldAsync(Agent($"parley-facilitator@{DeadProject}"), cancellationToken: token));

        Assert.True(facilitator.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition);
    }

    private const string DeadProject = "alpha";

    [Fact]
    public async Task Resuming_an_agent_that_owes_work_starts_it_as_a_restart_would()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always });
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();
        _ = await console.HoldAsync(Agent("orders@alpha"), cancellationToken: token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);
        _ = await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "orders@alpha", "waiting for you", "c1"), cancellationToken: token);

        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));

        _ = await console.ResumeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(500), Patience));
    }

    [Fact]
    public async Task Resuming_an_agent_that_owes_nothing_starts_nobody()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always });
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();
        _ = await console.HoldAsync(Agent("orders@alpha"), cancellationToken: token);
        _ = await console.ResumeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_hold_is_read_back_by_the_registry()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();

        Assert.Empty((await console.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows
            .Single(r => r.Name == "orders@alpha").HeldUtc);

        _ = await console.HoldAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.NotEmpty((await console.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows
            .Single(r => r.Name == "orders@alpha").HeldUtc);

        _ = await console.ResumeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.Empty((await console.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows
            .Single(r => r.Name == "orders@alpha").HeldUtc);
    }

    [Fact]
    public async Task Holding_closes_nothing_and_answers_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);
        _ = await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "orders@alpha", "still open after", "c1"), cancellationToken: token);

        var console = broker.ConnectAsOperator();
        _ = await console.HoldAsync(Agent("orders@alpha"), cancellationToken: token);

        var open = await console.ListConversationsAsync(new Wire.ListConversationsRequest(), cancellationToken: token);

        Assert.True(Assert.Single(open.Conversations).HasOpenExchange);
    }

    [Fact]
    public void An_agent_cannot_hold_or_resume_anybody()
    {
        // The Agents service is what an adapter binds. The operations are the person's alone (SPEC.md §10.2).
        var agentOperations = typeof(Wire.Agents.AgentsClient).GetMethods().Select(m => m.Name);

        Assert.DoesNotContain(agentOperations, name => name.StartsWith("Hold", StringComparison.Ordinal));
        Assert.DoesNotContain(agentOperations, name => name.StartsWith("Resume", StringComparison.Ordinal));
    }
}
