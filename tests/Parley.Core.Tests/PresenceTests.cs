using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// How reachable an agent is, read from connections.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a suite about time windows, and the windows are gone rather than adjusted.
/// Liveness used to be derived from a timestamp: three sweeps of silence meant one thing, five
/// minutes meant another, and both were estimates that every reader had to interpret using a
/// sweep cadence it could not see. The tests that pinned those boundaries were pinning the
/// arithmetic of a guess.
/// </para>
/// <para>
/// There is nothing left to pin to the second. A connection is open or it is not.
/// </para>
/// </remarks>
public sealed class PresenceTests
{
    [Theory]
    [InlineData(true, true, AgentPresence.Running)]
    [InlineData(true, false, AgentPresence.Startable)]
    [InlineData(false, false, AgentPresence.Stranded)]
    public void Presence_is_the_two_connections(
        bool node, bool agent, AgentPresence expected) =>
        Assert.Equal(expected, AgentLiveness.Read(node, agent));

    [Fact]
    public void An_agent_with_its_own_connection_is_running_whatever_its_node_says()
    {
        // This asserted the opposite until the node process was built, and the reversal is worth
        // recording rather than quietly editing.
        //
        // The old rule was that a disconnected node outranked everything, because a node going
        // down and its agents being marked disconnected were two writes and a reader trusting
        // the second without the first would believe a dead machine's agents were running.
        //
        // SPEC.md §4 removes the premise: a node is not a relay, and its agents speak to the
        // broker through their own adapters under their own identities. The two connections are
        // independent, one broker observes both, and each is decremented by its own socket
        // closing -- so an agent whose stream is open is reachable however its machine's
        // supervisor is faring. A machine may have no node at all and working agents on it,
        // which is what happens whenever somebody opens an editor without running a supervisor
        // there, and under the old rule every one of those read as unreachable while it was
        // answering questions.
        Assert.Equal(
            AgentPresence.Running,
            AgentLiveness.Read(nodeConnected: false, agentConnected: true));
    }

    [Fact]
    public void A_node_that_is_gone_leaves_its_idle_agents_stranded()
    {
        // What the node's connection does decide, and the only thing it decides: whether an
        // agent that is not running can be made to run. Nothing else on that machine is left to
        // start it, so Stranded is a statement about the machine rather than about the agent.
        Assert.Equal(
            AgentPresence.Stranded,
            AgentLiveness.Read(nodeConnected: false, agentConnected: false));
    }

    [Fact]
    public void Knowing_nothing_does_not_read_as_knowing_the_agent_is_up()
    {
        // The default value of the enum, asserted because it is a decision rather than an
        // accident of declaration order. A default-constructed presence is reachable -- records
        // carry one and a caller can omit it -- and a default of Running would be a claim about
        // an agent that nobody had looked at.
        Assert.Equal(AgentPresence.Stranded, default(AgentPresence));
    }

    [Theory]
    [InlineData(AgentStatus.Active, true)]
    [InlineData(AgentStatus.Retired, false)]
    public void Only_retirement_gates_routing(AgentStatus status, bool routable) =>
        Assert.Equal(routable, AgentLiveness.Routable(status));

    [Fact]
    public void Presence_never_gates_routing()
    {
        // The load-bearing one, and the mistake this project has already made once: making an
        // unreachable agent refuse traffic broke half the broker suite, because a question to an
        // agent nobody is running is precisely what causes it to be started.
        //
        // Written as a signature check rather than a behaviour check because that is what it is:
        // Routable cannot consider presence, since it is not given any. A future edit that
        // wanted to would have to change the signature, which is the moment to reread §14.
        var takesPresence = typeof(AgentLiveness)
            .GetMethod(nameof(AgentLiveness.Routable))!
            .GetParameters()
            .Any(p => p.ParameterType == typeof(AgentPresence));

        Assert.False(takesPresence);
    }
}
