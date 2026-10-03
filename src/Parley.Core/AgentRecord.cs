// The references to AgentRepository below are prose rather than links. It lives in Parley.Store,
// which this project may not reference -- the absence is the design, so the link cannot be made
// and the fact it named is still worth keeping.
// Deliberately not AgentSummary, and the two must never be merged.
//
// They differ by exactly what SPEC.md §10.1 withholds from an agent: the workspace directory, the
// session reference, the adapter's configuration. AgentSummary is what a peer may see; this is
// what the person running the deployment may. Side by side in one project they look like
// duplication and are the opposite -- a single shape would mean one of the two surfaces returns
// more than its section allows, and which one would depend on who wrote the mapping.
namespace Parley.Core;

/// <summary>
/// A registered participant: its identity, how to reach it, and whether it is currently
/// attached.
/// </summary>
/// <param name="Name">The agent's stable logical name.</param>
/// <param name="Adapter">
/// The adapter that attaches this agent, identifying which integration the broker should use
/// to reach it.
/// </param>
/// <param name="Tier">The adapter's capability tier.</param>
/// <param name="Workspace">
/// The directory the agent operates in, or <see langword="null"/> for agents with no
/// filesystem scope such as a model-API responder.
/// </param>
/// <param name="SessionRef">
/// An adapter-specific handle for resuming a previous session, or <see langword="null"/> if
/// none exists yet. Mutable: reassigned whenever the agent is spawned or resumed.
/// </param>
/// <param name="AdapterConfig">
/// Whatever the named adapter needs to invoke this agent — a model identifier, an endpoint, a
/// timeout, a notification channel — as a JSON document, or <see langword="null"/> if it needs
/// nothing. Opaque to the broker by design: it is stored and handed back at invocation, and
/// only the adapter interprets it. Credentials do not belong here; see
/// <c>AgentRepository.SetCredentialAsync</c>.
/// </param>
/// <param name="Connected">
/// When the agent last communicated, or <see langword="null"/> if it never has. Used to
/// bound the change summary injected on resume, so a returning agent is not reasoning about
/// code that moved while it was away.
/// </param>
/// <param name="Status">The agent's current registration and liveness state.</param>
/// <param name="Presence">
/// How reachable it is, read at the same moment as the rest of the row and never stored.
/// Defaulted rather than required because a record being written has no presence to state: the
/// value is an observation the read makes, and a caller constructing one to save it is not in a
/// position to observe anything. The default is
/// <see cref="AgentPresence.Stranded"/> for the same reason it is the enum's zero — knowing
/// nothing must not read as knowing the agent is up.
/// </param>
/// <param name="Owns">
/// One line saying what this agent owns, or <see langword="null"/> if its launch did not say.
/// Unlike the rest of this record it is written for peers to read: see
/// <see cref="OwnershipStatement"/>, and <c>AgentRepository.RefreshOwnershipAsync</c>
/// for why it is refreshed on every launch while the configuration around it is not.
/// </param>
/// <param name="OwnsAccepted">
/// What a person accepted as this agent's line, which is not what peers read until the agent
/// restarts. Null when nobody has accepted one. Held beside <paramref name="Owns"/> rather than
/// replacing it, because the two differing is the whole of what "pending" means here — see
/// <see cref="OwnsPending"/>. Defaulted for the reason <paramref name="Presence"/> is: a caller
/// constructing a record in order to save it is stating what the agent said about itself, and
/// what a person agreed to is somebody else's fact, written by a different operation.
/// </param>
/// <param name="OwnsSource">
/// How the accepted line was arrived at, or null when nobody has accepted one (SPEC.md §10.2 <c>registry()</c>).
/// Travels so a surface can show a line accepted automatically apart from one a person decided: the review's
/// whole worth is that a person can tell the two apart at a glance.
/// </param>
/// <param name="Turns">
/// What the agent's node has said about its turns (SPEC.md §11.6), or null where nobody has read it — which a surface
/// treats exactly as <see cref="TurnStanding.Unreported"/>. On the operator's registry only.
/// </param>
/// <remarks>
/// Credentials are deliberately not a field here, and should not become one. This record is
/// what <c>AgentRepository.UpsertAsync</c> writes from a manifest reload, and anything
/// carried on it is overwritten by that reload. Credentials are registered once and must
/// survive it — folding them in would mean a reload silently dropped every agent's
/// references and broke invocation on the next start. They are read and written separately,
/// through <c>AgentRepository.GetCredentialsAsync</c> and
/// <c>AgentRepository.SetCredentialAsync</c>, so that the two lifecycles cannot be
/// confused by accident.
/// </remarks>
public sealed record AgentRecord(
    AgentName Name,
    string Adapter,
    AgentTier Tier,
    string? Workspace,
    string? SessionRef,
    string? AdapterConfig,
    bool Connected,
    AgentStatus Status,
    string? Owns = null,
    AgentPresence Presence = AgentPresence.Stranded,
    string? OwnsAccepted = null,
    OwnershipSource? OwnsSource = null,
    TurnStanding? Turns = null)
{
    /// <summary>Whether this row is the facilitator's (SPEC.md principle 2.11).</summary>
    /// <remarks>
    /// Derived from the adapter rather than carried as a field, because storage ties that adapter to the reserved
    /// role in both directions — so there is one fact, and a flag beside it would be a second one to disagree.
    /// A surface shows such a row as Parley and offers nothing on it.
    /// </remarks>
    public bool Facilitator => string.Equals(Adapter, Facilitation.Adapter, StringComparison.Ordinal);

    /// <summary>Whether a person has accepted a line that peers do not read yet.</summary>
    /// <value><see langword="true"/> while the accepted line and the live one differ.</value>
    /// <remarks>
    /// <para>
    /// <b>A comparison of two facts rather than a flag anybody maintains.</b> <see cref="Owns"/> is
    /// filled from the launch environment when the agent attaches (SPEC.md §12.3) and no operation
    /// writes it; <see cref="OwnsAccepted"/> is what a person agreed to. They differ until the agent
    /// next starts, at which point they agree and this goes away on its own — so there is no third
    /// state and nothing to clear, which is the failure mode a flag has.
    /// </para>
    /// <para>
    /// A line accepted for an agent that never restarts is pending for ever, visibly. That is the
    /// intended behaviour: the alternative is a surface that stops mentioning a line peers have
    /// still never read.
    /// </para>
    /// </remarks>
    public bool OwnsPending =>
        OwnershipStatement.Read(OwnsAccepted) is { } accepted
        && !string.Equals(accepted, OwnershipStatement.Read(Owns), StringComparison.Ordinal);

    /// <summary>What this agent is for, from its name.</summary>
    public string Role => Name.Role;

    /// <summary>The machine reporting it, from its name.</summary>
    /// <remarks>
    /// Derived rather than stored on the record, so the two cannot disagree. They are separate
    /// columns in storage because a role query has to find every agent sharing a role without
    /// matching on a substring — <c>infra</c> is a prefix of <c>infrastructure</c> — but that is
    /// an indexing concern and not a second source of truth.
    /// </remarks>
    public string Machine => Name.Machine;
}
