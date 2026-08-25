using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// The operations of SPEC.md §10.2, available to the person running the broker and never to an
/// agent.
/// </summary>
/// <remarks>
/// <para>
/// A separate type because §10.2 opens by warning that an adapter surfacing any of these into an
/// agent's tool set would breach §10.1, and the protection the spec offers is a naming
/// convention — <c>force_conclude</c> is deliberately not called <c>conclude</c>, so that the
/// wrong one cannot be bound by accident. That is a real safeguard and a thin one. Keeping these
/// off <see cref="Broker"/> makes it structural instead: an adapter is handed a broker, and a
/// broker has no operator operations on it to bind.
/// </para>
/// <para>
/// It is a guard against the easy mistake, not a sandbox, and the difference is worth stating
/// so nobody mistakes it for isolation. An adapter references this assembly and gets
/// <c>Parley.Store</c> with it, so it can construct a repository and write whatever it likes —
/// as it could already, by saving an exchange directly. Nothing here is a privilege boundary;
/// the trust boundary is §13, at the message, and the process boundary is the deployment's.
/// What this removes is the specific failure §10.2 describes: the wrong method bound because it
/// was on the object already in hand and the signatures nearly matched.
/// </para>
/// <para>
/// The distinction these enforce is not a hierarchy among agents. No agent may conclude over
/// another's objection, and the person running the broker is not an agent — the same division as
/// <c>retire</c>. The broker never overrules a participant; the operator always may.
/// </para>
/// <para>
/// Deliberately not lifecycle-guarded, unlike every operation on <see cref="Broker"/>. Those
/// refuse before <see cref="Broker.StartAsync"/> because they decide against state that §14's
/// attach sequence exists to invalidate — this agent's debts, its liveness. These decide
/// against nothing: closing a conversation is an instruction, not an inference. Requiring a
/// started broker would make the escape hatch depend on the lifecycle an operator is most likely
/// reaching for it because of.
/// </para>
/// </remarks>
public sealed class OperatorConsole
{
    private readonly ConversationRepository _conversations;
    private readonly AgentRepository _agents;
    private readonly MessageRepository _messages;
    private readonly Pruner _pruner;
    private readonly SizeRefusalRepository _refusals;
    private readonly ClarifyReportRepository _clarify;

    /// <summary>
    /// Creates a console over an open connection.
    /// </summary>
    /// <param name="connection">
    /// An open, already-migrated connection to the project's database. Borrowed, never closed;
    /// the caller owns its lifetime, as with <see cref="Broker"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public OperatorConsole(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _conversations = new ConversationRepository(connection);
        _agents = new AgentRepository(connection);
        _messages = new MessageRepository(connection);
        _pruner = new Pruner(connection);
        _refusals = new SizeRefusalRepository(connection);
        _clarify = new ClarifyReportRepository(connection);
    }

    /// <summary>
    /// Closes a conversation regardless of its participants' votes.
    /// </summary>
    /// <param name="conversationId">The discussion to end.</param>
    /// <param name="nowUtc">The instant of closure.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if this call closed it; <see langword="false"/> if it had already
    /// concluded, in which case the original instant is left alone.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// It exists because a decline blocks indefinitely (SPEC.md §5.0) and the agent holding it
    /// may simply be wrong. Without it the closure poll introduces a state nothing can resolve:
    /// <see cref="Broker.TickAsync"/> will not conclude over an objection, and a decline lapses
    /// only if its holder is retired or becomes unreachable, so the conversation would stay
    /// polling for as long as the database survived.
    /// </para>
    /// <para>
    /// Closes a conversation in any non-terminal state, not only one that is polling. An operator
    /// ending a discussion has no reason to first find an agent willing to propose closure, and
    /// requiring a poll to exist would make the escape hatch depend on the machinery it exists to
    /// escape.
    /// </para>
    /// </remarks>
    public async Task<bool> ForceConcludeAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _conversations.ForceConcludeAsync(
            conversationId, nowUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Withdraws an agent's registration, per SPEC.md §10.2.
    /// </summary>
    /// <param name="agent">The agent to retire.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the registry changed.</returns>
    /// <remarks>
    /// <para>
    /// The one status the broker never sets on its own evidence. <c>unreachable</c> is the
    /// broker reporting that it tried to reach an agent and could not, which a re-clone or a
    /// restored backup invalidates at any moment; this is a person saying it will not come back.
    /// §14 turns on that difference: an attach clears every other status as stale liveness and
    /// leaves this one, so a retired agent can start its server as often as it likes and be
    /// refused every operation.
    /// </para>
    /// <para>
    /// It does not resolve what the agent already owes, and that is deliberate. Its debts orphan
    /// and are ghosted on the ordinary schedule of §14, which tells each asker with its own
    /// stated consequences — the same path any abandoned exchange takes. Retiring is about the
    /// registry, not the traffic.
    /// </para>
    /// </remarks>
    public async Task<bool> RetireAsync(
        AgentName agent,
        CancellationToken cancellationToken = default) =>
        await _agents.RetireAsync(agent, cancellationToken).ConfigureAwait(false) > 0;

    /// <summary>
    /// Withdraws a retirement, per SPEC.md §10.2.
    /// </summary>
    /// <param name="agent">The agent to reinstate.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the registry changed.</returns>
    /// <remarks>
    /// The only thing that clears a retirement. §14 is explicit that registration must not, and
    /// an agent attaching is the closest thing to registration there is — so without this the
    /// state would be terminal, and an operator who retired an agent by mistake would have no
    /// way back short of editing the database.
    /// </remarks>
    public async Task<bool> ReinstateAsync(
        AgentName agent,
        CancellationToken cancellationToken = default) =>
        await _agents.ReinstateAsync(agent, cancellationToken).ConfigureAwait(false) > 0;

    /// <summary>
    /// Describes every registered agent, whole.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored record of each agent.</returns>
    /// <remarks>
    /// <para>
    /// Named apart from <see cref="Broker.ListAgentsAsync"/> for the reason §10.2 gives about
    /// <c>force_conclude</c> and <c>conclude</c>: two operations that differ across the
    /// §10.1 boundary must not share a name, because the mistake that boundary guards against is
    /// the wrong one being bound when the signatures nearly match. <c>SurfaceBoundaryTests</c>
    /// fails if they ever collide.
    /// </para>
    /// <para>
    /// The difference is not cosmetic. This returns the stored record — workspace path, session
    /// handle, and the adapter configuration §12.1 makes free-form so it can carry endpoints —
    /// where the agent-facing operation returns a projection with none of it. An operator is
    /// looking at their own deployment; a peer is not.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentRecord>> DescribeAgentsAsync(
        CancellationToken cancellationToken = default) =>
        await _agents.GetAllAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reports what the size cap of rule 8.5 refused, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per agent and message type that has ever been refused, worst first.</returns>
    /// <remarks>
    /// <para>
    /// The evidence for the one number in this protocol that has never had any. Rule 8.5 fixes a
    /// message size; §17 admits the clarify cap is a guess and asks for instrumentation before
    /// tuning; neither has ever been measured. This is where the measuring shows up, and the next
    /// change to either number should cite it.
    /// </para>
    /// <para>
    /// An operator operation rather than an agent one, and not because the numbers are sensitive.
    /// It reads across every agent at once, which is the property that puts <c>export</c> here
    /// too — and an agent that could see how close its peers run to the cap would be reading
    /// something about how they work rather than what they own.
    /// </para>
    /// <para>
    /// No refused text is stored anywhere, so none can be returned. Keeping the payload that was
    /// refused for being too long would retain exactly what the cap exists to keep out and put it
    /// beyond the retention policy that governs messages.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<SizeRefusal>> DescribeRefusalsAsync(
        CancellationToken cancellationToken = default) =>
        await _refusals.GetAllAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reports what clarification cost, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The depth distribution and what it implies about the cap.</returns>
    /// <remarks>
    /// <para>
    /// An operator operation rather than an agent one, and for a different reason than
    /// <c>export</c>: this reads across every pair in the deployment, so an agent holding it
    /// would learn who talks to whom and how badly it goes — which §10.1 withholds. It is also
    /// not a thing an agent could act on. The decision it informs is what the cap should be, and
    /// no agent sets that.
    /// </para>
    /// <para>
    /// It returns counts and never text. The clarifications themselves are messages under the
    /// retention policy of §9.4, and a report that quoted them would put one agent's requests in
    /// front of an operator outside the envelope that marks them as somebody else's words.
    /// </para>
    /// </remarks>
    public async Task<ClarifyReport> DescribeClarificationsAsync(
        CancellationToken cancellationToken = default) =>
        await _clarify.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Emits every message in a conversation, per SPEC.md §10.2.
    /// </summary>
    /// <param name="conversationId">The discussion to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every message carrying that identifier, oldest first.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
    /// <remarks>
    /// <para>
    /// The one query that reads across agents, which is exactly why it is an operator operation:
    /// an adapter that surfaced it would hand one agent everything two others had said to each
    /// other, breaching §10.1 in the most direct way available.
    /// </para>
    /// <para>
    /// It returns the messages rather than the envelopes of §13. An envelope exists to mark a
    /// peer's words as data inside another agent's context, and there is no agent here — an
    /// operator reading their own deployment's log is not being asked to obey it. Wrapping would
    /// also make the export unusable for the purpose §10.2 gives it, which is comparing how
    /// different model pairings converge on the same question.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Message>> ExportAsync(
        string conversationId,
        CancellationToken cancellationToken = default) =>
        await _messages.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Applies the retention policy of SPEC.md §9.4 now, per §10.2.
    /// </summary>
    /// <param name="policy">The policy to apply.</param>
    /// <param name="nowUtc">The instant to measure ages against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What was removed, and what the floor held back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is unusable.</exception>
    /// <remarks>
    /// <para>
    /// §10.2 describes this as applying the policy "immediately rather than waiting for the
    /// sweep", which says plainly that it is not the only thing that prunes: every server's tick
    /// does the same work on its own schedule. What this adds is the ability to act at a
    /// particular moment — after retiring an agent, before handing a machine on, or once an
    /// operator has changed the numbers and wants the effect now rather than within a tick.
    /// </para>
    /// <para>
    /// It takes the policy as an argument rather than holding one, and that is the difference
    /// between this and every other operation here. The others are instructions whose effect is
    /// fully described by the verb; this one's effect depends entirely on two numbers, and a
    /// console that carried its own copy would let the operator's command and the servers' sweep
    /// apply different policies to the same file without either being able to say so. Passing it
    /// in forces the caller to have decided, which is what lets the caller report it.
    /// </para>
    /// <para>
    /// Not lifecycle-guarded, like the rest of this type. It also does not require an attached
    /// agent, which is the point: reclaiming a log is exactly what an operator does when the
    /// agents are not running.
    /// </para>
    /// </remarks>
    public async Task<PruneResult> PruneAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _pruner.PruneAsync(policy, nowUtc, cancellationToken).ConfigureAwait(false);
}
