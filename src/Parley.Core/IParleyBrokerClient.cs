namespace Parley.Core;

/// <summary>
/// What a conforming broker exposes to an operator console. Broker V1.
/// </summary>
/// <remarks>
/// <para>
/// This is the declaration of SPEC.md §10.2, and it is the capability list rather than a
/// description of one. A list in prose beside an interface is a second copy that drifts the
/// moment somebody adds a method; anything reading this already knows what a broker must do, and
/// the compiler fails whoever does not implement it.
/// </para>
/// <para>
/// <b>Clients adapt to the broker, not the broker to its clients.</b> What a console may do is
/// what this says and not what it happens to find on the far end. There are many clients and one
/// store, and a broker that bent to each of them would be several contracts wearing one name —
/// with the disagreement surfacing as one console showing something no other client can see.
/// <see cref="ImplementsAsync"/> is asked first for that reason: a contract whose version cannot
/// be discovered is one nobody can rely on, and without it a client cannot tell a broker of the
/// wrong version from one that is switched off.
/// </para>
/// <para>
/// <b>It carries no agent operation, and never will.</b> §10.1's surface is a different type for
/// a different caller; the two are kept apart structurally rather than by naming, because the
/// naming convention is what §10.2 opens by admitting is thin. The counterpart contract — what an
/// agent must be able to receive from the operator — is
/// <see cref="IAcceptsOperatorInstructions"/>, and it holds none of what is below.
/// </para>
/// <para>
/// Declared here, in the domain project, because it is the one place a client and a server can
/// both see and it drags in nothing: no transport, no storage, no I/O. An implementation over
/// gRPC and an implementation over anything else are both implementations of this.
/// </para>
/// </remarks>
public interface IParleyBrokerClient
{
    /// <summary>Which protocol version this broker speaks.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version, as <see cref="ProtocolVersion"/> renders one.</returns>
    Task<string> ImplementsAsync(CancellationToken cancellationToken = default);

    /// <summary>The whole registry, including the workspace each agent works in.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Every registered agent.</returns>
    /// <remarks>
    /// Returns <see cref="AgentRecord"/> and not <see cref="AgentSummary"/>. The two differ by
    /// exactly what §10.1 withholds from an agent, which is why this operation is named
    /// <c>registry</c> in the document rather than <c>agents</c>.
    /// </remarks>
    Task<IReadOnlyList<AgentRecord>> RegistryAsync(CancellationToken cancellationToken = default);

    /// <summary>Which conversations this broker holds, most recent activity first.</summary>
    /// <param name="participant">Restrict to conversations this agent is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those active at or before this instant, if given.</param>
    /// <param name="pageToken">Where to resume, or null for the first page.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One page of summaries, and the token for the next.</returns>
    /// <remarks>
    /// Exists because <see cref="ExportAsync"/> takes an identifier nothing else produces. The
    /// identifier is opaque and chosen by an agent (§5.0), so a person does not know one and
    /// cannot guess it.
    /// </remarks>
    Task<Page<ConversationSummary>> ConversationsAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>Every message in a conversation, and the state of every exchange they belong to.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The messages and the exchange states, read at one instant.</returns>
    Task<ConversationExport> ExportAsync(
        string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Who is waiting on whom.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One edge per agent waiting on another.</returns>
    /// <remarks>
    /// So that an operator can see a discussion nobody is going to finish. Reads across pairs,
    /// which is what makes it an operator operation. Edges rather than
    /// <see cref="WaitForGraph"/>: that type answers whether a new question would close a cycle
    /// and is never read back, which is right for its purpose and useless for showing somebody
    /// what is stuck.
    /// </remarks>
    Task<IReadOnlyList<WaitingEdge>> WaitingAsync(CancellationToken cancellationToken = default);

    /// <summary>What the size cap refused, per agent and message type.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The counts.</returns>
    Task<IReadOnlyList<SizeRefusal>> RefusalsAsync(CancellationToken cancellationToken = default);

    /// <summary>What clarification actually cost.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    Task<ClarifyReport> ClarificationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Where work was sent when it was out of scope.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    Task<ReferralReport> ReferralsAsync(CancellationToken cancellationToken = default);

    /// <summary>What Notify has carried, and to whom.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    /// <remarks>
    /// §17 question 1 leaves Notify uncapped on purpose; this is the evidence for whether that
    /// holds.
    /// </remarks>
    Task<NotifyVolumeReport> NotificationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Give an agent an instruction from the operator.</summary>
    /// <param name="agent">Who to instruct.</param>
    /// <param name="text">What to tell it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What became of it, and whatever the agent said.</returns>
    /// <remarks>
    /// The one operation here that puts a person's words into an agent's turn. It is safe because
    /// the operator is not a peer — principle 2.6 allows exactly one asymmetry and this is it —
    /// and it stays safe because no agent can reach this interface.
    /// </remarks>
    Task<InstructionResult> InstructAsync(
        AgentName agent, string text, CancellationToken cancellationToken = default);

    /// <summary>Withdraw an agent, so nothing routes to it and it is not listed.</summary>
    /// <param name="agent">Who to retire.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    Task<bool> RetireAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Withdraw a retirement.</summary>
    /// <param name="agent">Who to reinstate.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    Task<bool> ReinstateAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Delete an agent and every conversation it was party to.</summary>
    /// <param name="agent">Who to purge.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was destroyed.</returns>
    /// <remarks>
    /// Destroys its peers' side of those conversations too, and cannot be undone. A client that
    /// offers this without first saying what it will destroy has offered the wrong thing.
    /// </remarks>
    Task<PurgeOutcome> PurgeAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Close a conversation regardless of its participants' votes.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    Task<bool> ForceConcludeAsync(
        string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Apply the retention policy now rather than waiting for the sweep.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was removed, and what the floor held back.</returns>
    Task<PruneResult> PruneAsync(CancellationToken cancellationToken = default);
}
