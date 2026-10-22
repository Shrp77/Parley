using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>
/// What the agents page says about one agent's state and about what it claims to own.
/// </summary>
/// <remarks>
/// <para>
/// Two judgements, both small enough to look obvious in a view and both wrong in a way a view
/// cannot be tested for. They live here so the case can be written rather than read.
/// </para>
/// <para>
/// The first is that retirement outranks presence. An agent can be retired and still hold a
/// connection — nothing disconnects it, and §10.2's retire only stops it being routed to and
/// listed. A page that read presence first would show a retired agent as running, which is the
/// one state it certainly is not from the protocol's point of view.
/// </para>
/// <para>
/// The second is that an absent <c>owns</c> line is a fact and never a gap to fill. It is the
/// agent's own claim about its scope (§10.1), so a console that substituted a description — from
/// the workspace path, from the name, from anything — would be putting words in an agent's mouth
/// in the one place a reader would take them as the agent's.
/// </para>
/// </remarks>
public static class Roster
{
    /// <summary>What is shown in an agent's state column.</summary>
    /// <param name="agent">The agent, as the registry described it.</param>
    /// <returns>One word.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is null.</exception>
    public static string State(AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return agent.Status == AgentStatus.Retired
            ? "retired"
            : agent.Presence.ToString().ToLowerInvariant();
    }

    /// <summary>Whether this agent has stated what it owns.</summary>
    /// <param name="agent">The agent, as the registry described it.</param>
    /// <returns><see langword="true"/> when it has.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is null.</exception>
    /// <remarks>
    /// Whitespace counts as absent. An agent that registered with a blank line has said nothing,
    /// and rendering an empty cell would read as a column that failed rather than as a claim
    /// never made.
    /// </remarks>
    public static bool HasStatedWhatItOwns(AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return !string.IsNullOrWhiteSpace(agent.Owns);
    }

    /// <summary>What the other agents in a project say they own, for reading beside a proposal.</summary>
    /// <param name="registry">The project's registry, as it was last read.</param>
    /// <param name="about">The agent whose line is being decided, which is left out.</param>
    /// <returns>The others that have stated a line, by name, in roster order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Shown so a person can see an overlap, and nothing here decides anything about it</b>.
    /// Two agents both claiming the same area is a real state of a deployment and it is the
    /// person's to settle, or to leave: Principle II gives no agent authority over another, so a helper
    /// that refused an overlapping line, ranked the claims, or marked one as the winner would be
    /// arbitration arriving in the one place nobody would look for it.
    /// </para>
    /// <para>
    /// <b>Agents with no line are left out rather than listed as blank.</b> The point of reading the
    /// neighbours is to see what is already claimed; a list of names saying nothing is noise, and on this
    /// screen noise costs the rows a person needs for the proposal itself.
    /// </para>
    /// <para>
    /// The live line rather than an accepted one, because what a neighbour's peers read is what actually
    /// competes with the line being decided. A pending line is a decision that has not reached anybody.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string Name, string Owns)> Neighbours(
        IEnumerable<AgentRecord> registry, AgentName about)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return
        [
            .. registry
                .Where(agent => agent.Name != about)
                .Where(agent => agent.Status != AgentStatus.Retired)
                .Where(HasStatedWhatItOwns)
                .Select(agent => (agent.Name.Value, agent.Owns!)),
        ];
    }

    /// <summary>The marker put beside a line a person accepted that peers do not read yet.</summary>
    /// <param name="agent">The agent, as the registry described it.</param>
    /// <returns>The marker, or an empty string when the two halves agree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>One implementation, because it is a promise every surface makes.</b> A person who
    /// accepted a line and sees it in a roster will read the roster as the deployment — so a surface
    /// that showed the accepted line plainly would be saying peers route by something they have never
    /// read. What is shown stays the <em>live</em> line, which is what the roster is for, and this is
    /// appended to say a newer one is waiting.
    /// </para>
    /// <para>
    /// <b>Short on purpose.</b> A long mark scrolls off the end of a row in a half-width pane and cuts the
    /// one fact somebody opened the screen to read. The sentence explaining what to do about it belongs in
    /// the dialog that accepted the line, where there is room for it.
    /// </para>
    /// </remarks>
    public static string PendingMark(AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Worded to be true of both cases: an agent that has stated nothing has no older line for the
        // accepted one to be newer than, and that is the commonest way this state is reached.
        return agent.OwnsPending
            ? " (a line is accepted and peers do not read it yet)"
            : string.Empty;
    }
}
