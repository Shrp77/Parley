namespace Parley.Core;

/// <summary>
/// Reports how reachable an agent is, from what is known about connections.
/// </summary>
/// <remarks>
/// <para>
/// Presence is <b>observed</b>, never stored and never inferred from a timestamp. A stored claim
/// would need something to withdraw it, and a killed session or a machine that lost power is
/// exactly the case where nothing is left running to do so; a timestamp could only produce a
/// guess dressed as a status. A connection is open or it is not, and the process holding it says
/// so. There is no window, no cadence, and nothing to assume. See SPEC.md §4.0 and §14.
/// </para>
/// </remarks>
public static class AgentLiveness
{
    /// <summary>
    /// Reports an agent's presence.
    /// </summary>
    /// <param name="nodeConnected">Whether the node on the agent's machine is connected.</param>
    /// <param name="agentConnected">Whether the agent's own adapter is connected.</param>
    /// <returns>What to report.</returns>
    /// <remarks>
    /// <para>
    /// <b>The agent's own connection is checked first.</b> SPEC.md §4 says a node is not a relay:
    /// its agents speak to the broker through their own adapters under their own identities. So the
    /// two connections are independent, and a machine may perfectly well have a working agent and no
    /// node — somebody opened an editor without running a supervisor there. Asking about the node
    /// first would report that agent <see cref="AgentPresence.Stranded"/> while it was answering
    /// questions. The node's connection decides only whether an agent that is <em>not</em> running
    /// can be made to run, which is the one question it actually answers.
    /// </para>
    /// <para>
    /// Retirement is not considered. It is a judgement rather than an observation and it
    /// outranks all of this, so it is reported from <see cref="AgentStatus"/> by whoever puts
    /// the two together — keeping them apart is what stops a retired agent's registration being
    /// quietly resurrected by a node reconnecting.
    /// </para>
    /// </remarks>
    public static AgentPresence Read(bool nodeConnected, bool agentConnected) =>
        agentConnected ? AgentPresence.Running
        : nodeConnected ? AgentPresence.Startable
        : AgentPresence.Stranded;

    /// <summary>
    /// Reports a participant's presence, telling the person seat and the facilitator apart from agents.
    /// </summary>
    /// <param name="name">Who.</param>
    /// <param name="nodeConnected">Whether the node on its machine is connected; meaningless for the two hosted nowhere.</param>
    /// <param name="agentConnected">Whether its own connection is open.</param>
    /// <returns>What to report.</returns>
    /// <remarks>
    /// <para>
    /// <b>The person reads <see cref="AgentPresence.Here"/> or <see cref="AgentPresence.Away"/>, never anything else</b>
    /// (SPEC.md §14). With no machine, the agents' reading would give the person <see cref="AgentPresence.Stranded"/> — telling a
    /// peer the person could not be reached, which is untrue: the seat is always there and always asked.
    /// </para>
    /// <para>
    /// <b>The facilitator reads <see cref="AgentPresence.Running"/></b>, since it is present whenever its project is served
    /// and has no machine to be connected or not. Keyed on the name rather than the adapter: both roles are reserved and
    /// storage ties each to its adapter, so the name says the same thing and every caller has it.
    /// </para>
    /// </remarks>
    public static AgentPresence Read(AgentName name, bool nodeConnected, bool agentConnected) =>
        PersonSeat.Is(name) ? (agentConnected ? AgentPresence.Here : AgentPresence.Away)
        : Facilitation.Is(name) ? AgentPresence.Running
        : Read(nodeConnected, agentConnected);

    /// <summary>
    /// Whether traffic may be routed to an agent in this state.
    /// </summary>
    /// <param name="status">What an operator has decided about the registration.</param>
    /// <returns><see langword="true"/> unless the agent is retired.</returns>
    /// <remarks>
    /// <para>
    /// Presence is deliberately not a parameter, and that is the whole content of this method.
    /// A Request to a <see cref="AgentPresence.Startable"/> agent is what causes it to be
    /// started, so refusing on the grounds that nothing is running would refuse the very traffic
    /// that would fix it. A Request to a <see cref="AgentPresence.Stranded"/> one is admitted,
    /// waits, and is flagged to the person — where a refusal at the door would report only a
    /// moment that may have passed by the time the work mattered.
    /// </para>
    /// <para>
    /// SPEC.md §5.5 depends on the second of those: a Request to a role reaches agents on machines
    /// that are switched off precisely so the missing part of the work is flagged rather than
    /// silently absent.
    /// </para>
    /// </remarks>
    public static bool Routable(AgentStatus status) => status != AgentStatus.Retired;
}
