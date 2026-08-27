namespace Parley.Core;

/// <summary>
/// Reports how reachable an agent is, from what is known about connections.
/// </summary>
/// <remarks>
/// <para>
/// This type has been written three times and the two earlier versions are worth recording,
/// because each fixed the previous one's failure and introduced its own.
/// </para>
/// <para>
/// The first <b>stored</b> liveness: <c>live</c> was written on attach and again on every
/// sweep, and nothing at all wrote the way out. A session that was killed, crashed, or lost its
/// machine is exactly the case where nothing is left running to withdraw the claim, so a dead
/// agent's row read <c>live</c> for ever and every reader believed it.
/// </para>
/// <para>
/// The second <b>derived</b> liveness from a timestamp, which fixed that — a timestamp stops
/// advancing when the process stops, however impolitely. But it could only produce a guess
/// dressed as a status: three sweeps of silence meant one thing and five minutes meant another,
/// and both were really "we have not heard anything and are estimating what that implies."
/// Worse, every reader had to assume the deployment's sweep cadence to interpret them, and no
/// reader could see it — a registry that reported healthy agents as <c>NotResponding</c> would
/// have been the result of somebody raising the tick.
/// </para>
/// <para>
/// This one <b>observes</b>. A connection is open or it is not, and the process holding it says
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
    /// <b>The agent's own connection is checked first, and this is a correction.</b> An earlier
    /// version asked about the node first and treated its answer as final, on the grounds that
    /// an agent cannot be connected through a node that is not. That premise is false here and
    /// SPEC.md §4 says so in as many words: a node is not a relay, and its agents speak to the
    /// broker through their own adapters under their own identities. So the two connections are
    /// independent, and a machine may perfectly well have a working agent and no node — somebody
    /// opened an editor without running a supervisor there.
    /// </para>
    /// <para>
    /// Under the old order that agent read as <see cref="AgentPresence.Stranded"/> while it was
    /// answering questions, which is the worst of the three to be wrong about: it means
    /// unreachable, and it would have been reported for the agents most obviously reachable of
    /// all. The node's connection decides only whether an agent that is <em>not</em> running can
    /// be made to run, which is the one question it actually answers.
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
    /// Whether traffic may be routed to an agent in this state.
    /// </summary>
    /// <param name="status">What an operator has decided about the registration.</param>
    /// <returns><see langword="true"/> unless the agent is retired.</returns>
    /// <remarks>
    /// <para>
    /// Presence is deliberately not a parameter, and that is the whole content of this method.
    /// A question to a <see cref="AgentPresence.Startable"/> agent is what causes it to be
    /// started, so refusing on the grounds that nothing is running would refuse the very traffic
    /// that would fix it. A question to a <see cref="AgentPresence.Stranded"/> one is admitted,
    /// orphans on the ordinary schedule, and is ghosted — which tells the asker something, where
    /// a refusal at the door would tell it only about a moment that may have passed by the time
    /// the answer mattered.
    /// </para>
    /// <para>
    /// SPEC.md §5.5 depends on the second of those: a role question reaches agents on machines
    /// that are switched off precisely so a missing answer arrives as an orphan rather than as
    /// silence.
    /// </para>
    /// </remarks>
    public static bool Routable(AgentStatus status) => status != AgentStatus.Retired;
}
