using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That an agent nothing has ever registered can still be started.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that decides whether 006's plan was right, and it has a stop rule.</b> The
/// plan reversed 005's refusal to make a newly added agent startable, on the strength of two
/// readings: that SPEC.md §12.3 routes spawn-on-demand through §12's manifest rather than through
/// self-registration, and that <c>NodesService.RequestStart</c> guards only on the node's catalogue
/// naming the role, with no registry lookup anywhere on that path.
/// </para>
/// <para>
/// The second of those is a reading of this build's code, which is evidence about this build rather
/// than about the protocol. So it is turned into a test over a real socket. <b>If it fails, the
/// reasoning is wrong</b>: 005's amendment goes back, the surface stops promising a startable agent,
/// and nobody adds a registration path to rescue it — writing a registration on a repository's
/// behalf is the forgery §12.3 forbids, whoever writes it.
/// </para>
/// <para>
/// The fixture is the whole argument in one line: <c>startable</c> names a role, and nothing ever
/// attaches under it. There is no registration for the broker to find.
/// </para>
/// </remarks>
public sealed class StartOnDemandTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_role_nothing_has_registered_is_started_when_an_operator_asks()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        // The catalogue names `orders`. Nothing has ever attached as it, so the registry holds
        // nothing at all -- which is precisely the case 005 said could not be reached.
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var asked = await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = "an operator asked for it" },
            cancellationToken: token);

        Assert.True(asked.Changed, "the broker did not accept a start for a catalogued role");

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal("orders@alpha", command.Start.Agent);
    }

    [Fact]
    public async Task Starting_does_not_create_a_registration()
    {
        // The other half of §12.3, and the one that would make this operation a forgery if it were
        // false. What may register the agent is the adapter the node launches, under the identity
        // its launch environment fixes -- never the broker, and never on anybody's behalf.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = "a probe" },
            cancellationToken: token);

        await node.WaitForCommandAsync(Patience);

        var registry = await broker.ConnectAsOperator().DescribeRegistryAsync(
            new Wire.Empty(), cancellationToken: token);

        Assert.DoesNotContain(registry.Rows, row => row.Name == "orders@alpha");
    }

    [Fact]
    public async Task A_role_no_catalogue_names_is_refused_and_nothing_is_started()
    {
        // The guard that makes the operation safe to expose: a node starts what its own catalogue
        // permits and nothing else, so an operator cannot use this to run arbitrary things on a
        // machine they are not sitting at.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var asked = await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "billing@alpha", Because = "a probe" },
            cancellationToken: token);

        Assert.False(asked.Changed);
        await Assert.ThrowsAsync<TimeoutException>(
            () => node.WaitForCommandAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task A_machine_with_no_node_is_reported_rather_than_thrown()
    {
        // §5.5 asks machines that are switched off on purpose, so their silence arrives as an
        // orphan the asker is told about. An operator asking for a start gets the same treatment:
        // a false, not an exception.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var asked = await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@nowhere", Because = "a probe" },
            cancellationToken: token);

        Assert.False(asked.Changed);
    }

    [Fact]
    public async Task An_agent_that_is_already_attached_is_given_a_turn_rather_than_duplicated()
    {
        // Starting a process for an agent that already has one is how a deployment ends up with two
        // of something. What the traffic needs then is a turn, which is the distinction
        // BrokerRuntime.RequestAttention already draws and this operation inherits rather than
        // reinventing. The agent was started by its node (SPEC.md §11.7), so its node is the one that can give the turn.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token, startedBy: node);

        await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = "a probe" },
            cancellationToken: token);

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, command.CommandCase);
    }

    [Fact]
    public async Task An_agent_attached_outside_Parley_is_asked_for_nothing_and_the_sender_is_told()
    {
        // The counterpart of the test above. The same agent, attached with no token, is a session somebody opened in
        // an editor (SPEC.md §11.7): the node runs no resident for it, so a turn asked of the node could not be given
        // and a turn marked as coming would never come. Nobody is asked, and the sender hears why.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var outcome = Assert.Single((await billing.Client.RequestAsync(
            Requests.Open("billing@alpha", "orders@alpha", "who owns the retry policy"), cancellationToken: token)).Outcomes);

        Assert.True(outcome.Ok);
        Assert.Equal(Wire.WakeOutcome.OpenOutside, outcome.Wake);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));
    }
}
