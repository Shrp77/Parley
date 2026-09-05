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
    private readonly ExchangeRepository _exchanges;
    private readonly InstructionRepository _instructions;
    private readonly Pruner _pruner;
    private readonly SizeRefusalRepository _refusals;
    private readonly ClarifyReportRepository _clarify;
    private readonly ReferralReportRepository _referrals;
    private readonly NotifyVolumeRepository _notifications;

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
        _exchanges = new ExchangeRepository(connection);
        _instructions = new InstructionRepository(connection);
        _pruner = new Pruner(connection);
        _refusals = new SizeRefusalRepository(connection);
        _clarify = new ClarifyReportRepository(connection);
        _referrals = new ReferralReportRepository(connection);
        _notifications = new NotifyVolumeRepository(connection);
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
    /// Deletes an agent and every conversation it was party to, per SPEC.md §10.2.
    /// </summary>
    /// <param name="agent">The agent to purge.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was destroyed.</returns>
    /// <remarks>
    /// The opposite decision to <see cref="RetireAsync"/> rather than a stronger version of it,
    /// and the two are told apart by what the operator wants to keep. §14's argument for a
    /// tombstone is about preserving what an agent said, so retirement keeps everything and
    /// frees nothing. This is for when that record is the thing being removed: it frees the name
    /// and keeps nothing, including the halves of those conversations belonging to peers. It
    /// cannot be undone and it refuses nothing.
    /// </remarks>
    public async Task<PurgeOutcome> PurgeAsync(
        AgentName agent,
        CancellationToken cancellationToken = default) =>
        await _agents.PurgeAsync(agent, cancellationToken: cancellationToken).ConfigureAwait(false);

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
    public async Task<IReadOnlyList<AgentRecord>> DescribeRegistryAsync(
        CancellationToken cancellationToken = default) =>
        // Presence arrives with the read rather than being applied over it. The operator sees
        // exactly what a peer sees in the roster, from the same join, which is the property that
        // stopped holding when one of them derived liveness and the other read a column.
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
    /// Reports where the ownership map is being argued about, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The <c>out_of_scope</c> rate per pair, and who the work was sent to.</returns>
    /// <remarks>
    /// <para>
    /// §15 lists this as cheap to collect and disproportionately useful, and it is the one
    /// telemetry item that points at a fixable thing: two agents disagreeing about where a
    /// boundary runs, or one holding a stale idea of what the other owns.
    /// </para>
    /// <para>
    /// It names agents and counts, never text. What was said in a referral is a message under the
    /// retention policy of §9.4; what this reads is the record kept beside it so the signal
    /// outlives the prose.
    /// </para>
    /// </remarks>
    public async Task<ReferralReport> DescribeReferralsAsync(
        CancellationToken cancellationToken = default) =>
        await _referrals.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reports what Notify has carried, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Volume and size per sending pair, and what each recipient has received.</returns>
    /// <remarks>
    /// §17 question 1 records that nothing bounds Notify, and that the decision about whether
    /// anything should is to be taken from evidence rather than from a number somebody picked.
    /// This is that evidence: how many, how big, between whom, over what span.
    /// </remarks>
    public async Task<NotifyVolumeReport> DescribeNotificationsAsync(
        CancellationToken cancellationToken = default) =>
        await _notifications.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Says who is waiting on whom, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One edge per open exchange, waiter first.</returns>
    /// <remarks>
    /// <para>
    /// An operator operation because it reads across pairs. From inside a pair, a question nobody
    /// is going to answer looks exactly like one that has not been answered yet — the difference
    /// is only visible to somebody who can see every open exchange at once, which is precisely
    /// what §10.1 withholds from an agent.
    /// </para>
    /// <para>
    /// Edges rather than a <c>WaitForGraph</c>. That type exists to answer one question — would
    /// this new question close a cycle — and answers it without ever being read back, which is
    /// right for its purpose and useless for showing somebody what is stuck.
    /// </para>
    /// <para>
    /// Derived from the open set rather than stored, so it cannot go stale: an exchange that
    /// closed between two reads is simply not in the second one.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<WaitingEdge>> DescribeWaitingAsync(
        CancellationToken cancellationToken = default)
    {
        var open = await _exchanges.GetOpenAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // The waiter is whoever is not owed the next move. Taking the asker would be wrong after
        // a clarification: the answerer asked back, and it is the original asker who now owes.
        return
        [
            .. open.Select(e => new WaitingEdge(
                e.OwedBy == e.Answerer ? e.Asker : e.Answerer,
                e.OwedBy,
                e.Id))
        ];
    }

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
    public async Task<ConversationExport> ExportAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var messages = await _messages
            .GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);

        var exchanges = await _exchanges
            .GetSnapshotsAsync(conversationId, cancellationToken).ConfigureAwait(false);

        return new ConversationExport(messages, exchanges);
    }

    /// <summary>Records an instruction before anybody tries to deliver it.</summary>
    /// <param name="agent">Who it is for.</param>
    /// <param name="text">What the operator said.</param>
    /// <param name="nowUtc">When they said it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The instruction's identifier.</returns>
    public async Task<long> RecordInstructionAsync(
        AgentName agent,
        string text,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _instructions.RecordAsync(agent, text, nowUtc, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Records what became of an instruction.</summary>
    /// <param name="id">Which instruction.</param>
    /// <param name="outcome">What became of it.</param>
    /// <param name="reply">What the agent said, if anything.</param>
    /// <param name="nowUtc">When it settled.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether a pending instruction was settled.</returns>
    public async Task<bool> SettleInstructionAsync(
        long id,
        InstructionOutcome outcome,
        string? reply,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _instructions.SettleAsync(id, outcome, reply, nowUtc, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Lists conversations for SPEC.md §10.2, most recent activity first.
    /// </summary>
    /// <param name="participant">Restrict to conversations this agent is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="beforeLastActivityUtc">Resume point: only those last active strictly before it.</param>
    /// <param name="beforeId">The identifier at that resume point, breaking ties on the instant.</param>
    /// <param name="includeArchived">Whether to list archived conversations as well.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One page of summaries.</returns>
    /// <remarks>
    /// Exists because <see cref="ExportAsync"/> takes an identifier nothing else produces: it is
    /// opaque and chosen by an agent (§5.0), so a person does not know one and cannot guess it.
    /// Without this, the operator's entitlement to see the shape of a discussion could be
    /// exercised only by reading the database, which §4 gives to one process.
    /// </remarks>
    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        int limit = 50,
        DateTimeOffset? beforeLastActivityUtc = null,
        string? beforeId = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default) =>
        await _conversations.ListAsync(
            participant, sinceUtc, untilUtc, limit, beforeLastActivityUtc, beforeId,
            includeArchived, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Counts the archived conversations a listing with the same filters left out.
    /// </summary>
    /// <param name="participant">Restrict to conversations this agent is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many were omitted.</returns>
    /// <remarks>
    /// Part of the listing contract rather than a nicety a surface may add, because archiving does
    /// not stop a conversation receiving messages: a client that could not tell archived ones
    /// existed would be hiding traffic rather than filing it, and would have no way to know.
    /// </remarks>
    public async Task<int> CountArchivedConversationsAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default) =>
        await _conversations.CountArchivedAsync(
            participant, sinceUtc, untilUtc, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Puts a conversation out of the ordinary listing, or brings it back, per SPEC.md §10.2.
    /// </summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="archived">Whether it should be archived.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether anything changed.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
    /// <remarks>
    /// <para>
    /// <b>Filing, not muting.</b> Nothing about delivery changes, no exchange is affected, and no
    /// agent is told. It is an operator's decision about their own view of a deployment, made
    /// durable so every operator and every client sees the same one rather than each keeping its
    /// own — which is the difference between this and a preference a surface could have kept to
    /// itself.
    /// </para>
    /// <para>
    /// <b>A message arriving into an archived conversation does not un-archive it.</b> That was
    /// decided rather than allowed to emerge: the alternative lets any agent reverse an operator's
    /// decision by sending something, which is the one direction of authority this protocol does
    /// not have. What stops a busy conversation hiding is the omitted count above.
    /// </para>
    /// </remarks>
    public async Task<bool> SetConversationArchivedAsync(
        string conversationId,
        bool archived,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _conversations.SetArchivedAsync(
            conversationId, archived, nowUtc, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Destroys a conversation and everything belonging to it, per SPEC.md §10.2 and §14.
    /// </summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="nowUtc">The current instant, recorded on what is left behind.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What was destroyed, per table.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
    /// <remarks>
    /// <para>
    /// <b>Every participant's half goes.</b> That is not collateral being tolerated; it is what
    /// deleting a conversation means, since one with a side removed is a record that misleads
    /// whoever reads it next — §14's argument about an agent, applied to a narrower subject.
    /// </para>
    /// <para>
    /// <b>An open exchange inside it is not orphaned and not ghosted: it ceases to exist</b>, so
    /// the agent waiting on it is never told anything at all — not an answer, not a refusal, not
    /// the ghost §7 guarantees for a question nobody answers. That is a different outcome from
    /// every other way an exchange can end, and a surface offering this without naming it first
    /// has warned nobody.
    /// </para>
    /// </remarks>
    public async Task<DestroyOutcome> DestroyConversationAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _conversations.DestroyAsync(
            conversationId, nowUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

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
