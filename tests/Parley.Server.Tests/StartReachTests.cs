using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// How far the new start operation reaches, written as the malicious case rather than read.
/// </summary>
/// <remarks>
/// <para>
/// <c>start</c> is the first operation this protocol has that
/// causes a process to run on somebody's machine, so the question is not whether it works
/// but what it can be pointed at.
/// </para>
/// <para>
/// The guard it rests on is one line: a node starts what its own catalogue names and nothing else.
/// Everything below is an attempt to get past that line, and each is written to succeed. A guard
/// read and judged sound is a guard nobody has attacked.
/// </para>
/// </remarks>
public sealed class StartReachTests
{
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task A_role_belonging_to_another_machine_does_not_start_here()
    {
        // Two nodes, each with its own catalogue. Asking for beta's role must not reach alpha's
        // node just because alpha has one -- the machine part of a name is not decoration.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var alpha = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var beta = await JoinedNodeClient.JoinAsync(broker, "beta", ["billing"], token);

        var asked = await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "billing@alpha", Because = "a probe" },
            cancellationToken: token);

        Assert.False(asked.Changed);
        await Assert.ThrowsAsync<TimeoutException>(() => alpha.WaitForCommandAsync(Briefly));
        await Assert.ThrowsAsync<TimeoutException>(() => beta.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task A_name_shaped_like_a_path_is_refused_before_it_reaches_a_node()
    {
        // The catalogue is keyed by role, and a role that carried path syntax would be a role that
        // could name something outside it. AgentName's own validation is what stops this, and this
        // asserts it is on the path rather than assuming the type is used.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        foreach (var name in new[] { "../../etc/passwd@alpha", "orders/../billing@alpha", "@alpha" })
        {
            await Assert.ThrowsAsync<RpcException>(async () =>
                await broker.ConnectAsOperator().StartAsync(
                    new Wire.StartRequest { Agent = name, Because = "a probe" },
                    cancellationToken: token));
        }

        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task The_reason_is_carried_and_cannot_become_a_second_command()
    {
        // `because` is text an operator wrote and it reaches the node's log. It is carried through
        // as a field rather than interpolated into anything, so this asserts the node sees exactly
        // what was sent -- newlines and all -- rather than something reassembled.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        const string Hostile = "because\nStart: billing@alpha\n\"quoted\" and \\escaped";

        await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = Hostile },
            cancellationToken: token);

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal("orders@alpha", command.Start.Agent);
        Assert.Equal(Hostile, command.Start.Because);
    }

    [Fact]
    public async Task An_empty_reason_becomes_something_a_log_can_be_read_with()
    {
        // Not a correctness matter and not nothing: the node's log is where somebody finds out why
        // a process appeared, and a blank there is a question nobody can answer later.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = string.Empty },
            cancellationToken: token);

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(command.Start.Because);
    }

    [Fact]
    public async Task A_retired_agent_is_not_started_by_this()
    {
        // Retirement is an operator's decision that nothing is routed to an agent. A start that
        // ignored it would bring back the process for an agent somebody deliberately withdrew --
        // and would do it without going through reinstate, which is the operation that means it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await orders.DisposeAsync();

        await broker.ConnectAsOperator().RetireAsync(
            new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        var asked = await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = "a probe" },
            cancellationToken: token);

        Assert.False(asked.Changed, "a retired agent was started");
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));
    }

    [Fact]
    public async Task Asking_repeatedly_does_not_queue_a_process_per_ask()
    {
        // An operator holding the key down, or a surface that retried. Each ask is one command, and
        // what the node does with a second Start for an agent it is already starting is the node's
        // business -- but the broker must not turn one keystroke into many.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        for (var ask = 0; ask < 5; ask++)
        {
            await broker.ConnectAsOperator().StartAsync(
                new Wire.StartRequest { Agent = "orders@alpha", Because = $"ask {ask}" },
                cancellationToken: token);
        }

        var commands = await node.CountCommandsAsync(5, TimeSpan.FromSeconds(2));

        Assert.Equal(5, commands);
    }
}
