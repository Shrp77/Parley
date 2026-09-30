using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// The operations of SPEC.md §10.2, available to the person running the deployment and never to an agent.
/// </summary>
/// <remarks>
/// <para>
/// A separate type because §10.2 opens by warning that an adapter surfacing any of these into an agent's
/// tool set would breach §10.1, and the protection the document offers is a naming convention —
/// <c>force_conclude</c> is deliberately not <c>conclude</c>. Keeping these off <see cref="Broker"/> makes
/// that structural: an adapter is handed a broker, and a broker has no operator operation to bind.
/// </para>
/// <para>
/// It is a guard against the easy mistake, not a sandbox. The trust boundary is §13, at the message; what
/// this removes is the wrong method bound because it was on the object already in hand.
/// </para>
/// <para>
/// Not lifecycle-guarded, unlike <see cref="Broker"/>: closing a conversation or reading the flags decides
/// against nothing an attach invalidates, and requiring a started broker would make these depend on the
/// lifecycle a person is most likely reaching for them because of.
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
    private readonly ResultsReportRepository _results;
    private readonly FlagRepository _flags;

    /// <summary>Creates a console over an open connection.</summary>
    /// <param name="connection">An open, already-migrated connection. Borrowed, never closed.</param>
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
        _results = new ResultsReportRepository(connection);
        _flags = new FlagRepository(connection);
    }

    /// <summary>Closes a conversation on the person's word, regardless of its participants' votes (§5.0).</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="reason">Why, told to every participant holding open work; or <see langword="null"/>.</param>
    /// <param name="nowUtc">The instant of closure.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> if this call closed it; <see langword="false"/> if it was already closed.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// How the person ends work. Every exchange still open ends <c>closed</c>, and every participant holding
    /// one is sent the broker's <c>closed</c> Result in the same write (§14), so nobody goes on working on
    /// something the person ended and nobody waits for a Result that is not coming.
    /// </para>
    /// <para>
    /// Closes a conversation in any state short of closed: a person ending work should not have to wait for
    /// a participant to propose it, and a decline that blocks a poll indefinitely is exactly when this is
    /// reached for.
    /// </para>
    /// </remarks>
    public async Task<bool> ForceConcludeAsync(
        string conversationId,
        string? reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _conversations.ForceConcludeAsync(
            conversationId, reason, nowUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Withdraws an agent's registration (§10.2).</summary>
    /// <param name="agent">The agent to retire.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the registry changed.</returns>
    /// <remarks>
    /// The one status the broker never sets on its own evidence. It does not close what the agent owes:
    /// that work stays open and is flagged to the person (§14), because somebody asked for it and the
    /// requester has to find another way to get it done.
    /// </remarks>
    public async Task<bool> RetireAsync(AgentName agent, CancellationToken cancellationToken = default) =>
        await _agents.RetireAsync(agent, cancellationToken).ConfigureAwait(false) > 0;

    /// <summary>Records that a person accepted a statement of what an agent owns (§10.2).</summary>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="acceptance">What was agreed, and how they arrived at it.</param>
    /// <param name="nowUtc">When it was accepted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was recorded, and what peers read now beside it.</returns>
    /// <remarks>
    /// <para>
    /// <b>It does not write <c>owns</c>.</b> The registry is filled from the launch environment when
    /// the agent attaches (§12.3), so a line takes effect on the agent's next start — and the record
    /// carries what peers read now so a surface can say the two differ without keeping a flag.
    /// </para>
    /// <para>
    /// The statement is checked here as well as in storage, because the two answer different
    /// questions: storage refuses what must never be held, and this returns the reason in the words
    /// an operator reads rather than a constraint's name. §9.3's rule is that the invariant lives in
    /// storage; it does not say the only message a person gets should be SQLite's.
    /// </para>
    /// </remarks>
    public async Task<OwnershipRecord> RecordOwnershipAsync(
        AgentName agent,
        OwnershipAcceptance acceptance,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptance);

        // A person's call never records the machine's decision (SPEC.md §10.2): the facilitator records
        // `automatic` itself, below this surface, and a surface that could send it would let a person's edit
        // read as the agent's untouched words.
        if (acceptance.Source == OwnershipSource.Automatic)
        {
            return new OwnershipRecord(
                Recorded: false,
                "a line is recorded as accepted automatically only by the broker itself. A person accepting " +
                "the agent's words records them as proposed.",
                LiveStatement: null);
        }

        if (acceptance.Problem() is { } wrong)
        {
            return new OwnershipRecord(Recorded: false, wrong, LiveStatement: null);
        }

        var (recorded, live) = await _agents
            .RecordOwnershipAsync(agent, acceptance, nowUtc, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!recorded)
        {
            return new OwnershipRecord(
                Recorded: false,
                $"nothing has registered under '{agent.Value}', so nothing can be addressed to it " +
                "and there is no line to accept. Start it and it registers as it attaches.",
                LiveStatement: null);
        }

        return new OwnershipRecord(Recorded: true, Problem: null, live)
        {
            Pending = !string.Equals(live, acceptance.Statement, StringComparison.Ordinal)
        };
    }

    /// <summary>Withdraws a retirement (§10.2).</summary>
    /// <param name="agent">The agent to reinstate.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the registry changed.</returns>
    /// <remarks>The only thing that clears a retirement; registration must not (§14).</remarks>
    public async Task<bool> ReinstateAsync(AgentName agent, CancellationToken cancellationToken = default) =>
        await _agents.ReinstateAsync(agent, cancellationToken).ConfigureAwait(false) > 0;

    /// <summary>Deletes an agent and every conversation it was party to (§10.2).</summary>
    /// <param name="agent">The agent to purge.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was destroyed.</returns>
    /// <remarks>
    /// The opposite decision to <see cref="RetireAsync"/> rather than a stronger version of it: retirement
    /// keeps the record and frees nothing, and this frees the name and keeps nothing, including the other
    /// participants' parts of those conversations.
    /// </remarks>
    public async Task<PurgeOutcome> PurgeAsync(AgentName agent, CancellationToken cancellationToken = default) =>
        await _agents.PurgeAsync(agent, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Describes every registered agent, whole (§10.2's <c>registry()</c>).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored record of each agent.</returns>
    /// <remarks>
    /// Named apart from <see cref="Broker.ListAgentsAsync"/> because the two differ by exactly what §10.1
    /// withholds from an agent — the workspace, the session handle, the adapter configuration.
    /// </remarks>
    public async Task<IReadOnlyList<AgentRecord>> DescribeRegistryAsync(CancellationToken cancellationToken = default) =>
        await _agents.GetAllAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Reports what the size cap of rule 8.4 refused (§10.2).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per agent and message type that has ever been refused.</returns>
    /// <remarks>No refused text is stored anywhere, so none can be returned.</remarks>
    public async Task<IReadOnlyList<SizeRefusal>> DescribeRefusalsAsync(CancellationToken cancellationToken = default) =>
        await _refusals.GetAllAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Reports what clarification cost (§10.2).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The depth distribution and the caps in force.</returns>
    public async Task<ClarifyReport> DescribeClarificationsAsync(CancellationToken cancellationToken = default) =>
        await _clarify.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Reports what Results have said (§10.2's <c>results()</c>).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Outcomes by responder, blockers by kind, sources, and how conversations closed.</returns>
    /// <remarks>
    /// Names and counts, never text, read from records kept past retention (§9.3). An operator operation
    /// because it reads across every participant at once.
    /// </remarks>
    public async Task<ResultsReport> DescribeResultsAsync(CancellationToken cancellationToken = default) =>
        await _results.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Reports the work in front of the person (§10.2's <c>flags()</c>).</summary>
    /// <param name="nowUtc">The instant to measure silence against.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Open exchanges gone quiet or owed by a retired agent, and cycles in conversations still open.</returns>
    /// <remarks>
    /// What replaced ghosting (§6, §14). Nothing ends a piece of work but its participants or its person,
    /// so work that stops moving is shown here, and the person — who can see why — starts the agent, asks
    /// it, or closes the conversation.
    /// </remarks>
    public async Task<FlagReport> DescribeFlagsAsync(
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        await _flags.ReadAsync(nowUtc, cancellationToken).ConfigureAwait(false);

    /// <summary>Says who is waiting on whom (§10.2's <c>waiting()</c>).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One edge per open exchange, waiter first.</returns>
    /// <remarks>
    /// An operator operation because it reads across every exchange at once, which §10.1 withholds from a
    /// participant. Derived from the open set rather than stored, so it cannot go stale.
    /// </remarks>
    public async Task<IReadOnlyList<WaitingEdge>> DescribeWaitingAsync(CancellationToken cancellationToken = default)
    {
        var open = await _exchanges.GetOpenAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return [.. open.Select(e => new WaitingEdge(e.Waiting, e.OwedBy, e.Id))];
    }

    /// <summary>Emits every message in a conversation and the state of its exchanges (§10.2's <c>export()</c>).</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The messages, the exchange states, or the record of a destroyed conversation.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
    /// <remarks>
    /// <para>
    /// The one query that reads across participants, which is exactly why it is here: an adapter that
    /// surfaced it would hand one agent everything two others said to each other.
    /// </para>
    /// <para>
    /// It returns messages rather than envelopes. An envelope marks a participant's words as data inside
    /// another agent's context, and a person reading their own deployment's log is not being asked to obey it.
    /// </para>
    /// </remarks>
    public async Task<ConversationExport> ExportAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var messages = await _messages.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var exchanges = await _exchanges.GetSnapshotsAsync(conversationId, cancellationToken).ConfigureAwait(false);

        // Asked only when there is nothing else to say: an empty export cannot be told from a store that
        // lost something, and telling those apart is why the record is kept (§14).
        var destroyed = messages.Count == 0
            ? await _conversations.GetDestroyedAsync(conversationId, cancellationToken).ConfigureAwait(false)
            : null;

        return new ConversationExport(messages, exchanges, destroyed);
    }

    /// <summary>Records an instruction before anybody tries to deliver it.</summary>
    /// <param name="agent">Who it is for.</param>
    /// <param name="text">What the person said.</param>
    /// <param name="nowUtc">When they said it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The instruction's identifier.</returns>
    public async Task<long> RecordInstructionAsync(
        AgentName agent,
        string text,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _instructions.RecordAsync(agent, text, nowUtc, cancellationToken).ConfigureAwait(false);

    /// <summary>Reads instructions back, newest first (§10.2's <c>instructions()</c>).</summary>
    /// <param name="agent">Narrow to one agent, or null for every agent.</param>
    /// <param name="id">Narrow to exactly one instruction, or null for all that match.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="beforeId">Resume before this identifier, or null to start at the newest.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The instructions.</returns>
    /// <remarks>
    /// On this side of the boundary: an agent able to read one could learn what the person told another
    /// agent to do. It exists because a reply reachable only through the call that issued the instruction
    /// is lost the moment that call is.
    /// </remarks>
    public async Task<IReadOnlyList<InstructionRecord>> ListInstructionsAsync(
        AgentName? agent = null,
        long? id = null,
        int limit = 50,
        long? beforeId = null,
        CancellationToken cancellationToken = default) =>
        await _instructions.ListAsync(agent, id, limit, beforeId, cancellationToken).ConfigureAwait(false);

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
        await _instructions.SettleAsync(id, outcome, reply, nowUtc, cancellationToken).ConfigureAwait(false);

    /// <summary>Lists conversations, most recent activity first (§10.2's <c>conversations()</c>).</summary>
    /// <param name="participant">Restrict to conversations this participant is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="beforeLastActivityUtc">Resume point: only those last active strictly before it.</param>
    /// <param name="beforeId">The identifier at that resume point, breaking ties on the instant.</param>
    /// <param name="includeArchived">Whether to list archived conversations as well.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One page of summaries.</returns>
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
            participant, sinceUtc, untilUtc, limit, beforeLastActivityUtc, beforeId, includeArchived,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Counts the archived conversations a listing with the same filters left out.</summary>
    /// <param name="participant">Restrict to conversations this participant is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many were omitted.</returns>
    public async Task<int> CountArchivedConversationsAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default) =>
        await _conversations.CountArchivedAsync(participant, sinceUtc, untilUtc, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Puts a conversation out of the ordinary listing, or brings it back (§10.2).</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="archived">Whether it should be archived.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether anything changed.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
    /// <remarks>
    /// Filing, not muting: delivery is unchanged and no participant is told. A message arriving into an
    /// archived conversation does not un-archive it, because that would let any participant reverse a
    /// person's decision by sending something.
    /// </remarks>
    public async Task<bool> SetConversationArchivedAsync(
        string conversationId,
        bool archived,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _conversations.SetArchivedAsync(
            conversationId, archived, nowUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Destroys a conversation and everything belonging to it (§10.2, §14).</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="nowUtc">The current instant, recorded on what is left.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What was destroyed, per table.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
    /// <remarks>
    /// <b>An open exchange inside it is not closed: it ceases to exist</b>, so a participant waiting on it
    /// is never told anything at all — not a Result, not the <c>closed</c> Result a person's close would
    /// send. Closing first and destroying afterwards is how a person ends work and removes its record
    /// without leaving anybody waiting (§14).
    /// </remarks>
    public async Task<DestroyOutcome> DestroyConversationAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _conversations.DestroyAsync(conversationId, nowUtc, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Applies the retention policy of §9.4 now (§10.2's <c>prune()</c>).</summary>
    /// <param name="policy">The policy to apply.</param>
    /// <param name="nowUtc">The instant to measure ages against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What was removed, and what the floor held back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is unusable.</exception>
    /// <remarks>
    /// Takes the policy rather than holding one, so the caller has decided which numbers are in force and
    /// can report them.
    /// </remarks>
    public async Task<PruneResult> PruneAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _pruner.PruneAsync(policy, nowUtc, cancellationToken).ConfigureAwait(false);
}
