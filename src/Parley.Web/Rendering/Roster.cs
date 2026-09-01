using Parley.Core;

namespace Parley.Web.Rendering;

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
}
