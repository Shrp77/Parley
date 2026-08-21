using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Reads and writes exchanges, and owns admission control for new ones.
/// </summary>
/// <remarks>
/// <para>
/// Both concurrency rules live here rather than in the aggregate, because both need to see
/// every open exchange rather than just one: pair mutual exclusion, and rejection of
/// questions that would close a cycle in the wait-for graph.
/// </para>
/// <para>
/// Instances are cheap and hold no state beyond the supplied connection. They are not
/// thread-safe, because a <see cref="SqliteConnection"/> is not; give each unit of work its
/// own connection and repository.
/// </para>
/// </remarks>
/// <param name="connection">
/// An open connection to the Parley database, already migrated by
/// <see cref="SchemaMigrator"/>.
/// </param>
public sealed class ExchangeRepository(SqliteConnection connection)
{

    private const string SelectColumns =
        """
        SELECT id                AS Id,
               pair_key          AS PairKey,
               asker             AS Asker,
               answerer          AS Answerer,
               owed_by           AS OwedBy,
               state             AS State,
               clarify_depth     AS ClarifyDepth,
               clarify_depth_cap AS ClarifyDepthCap,
               delivery_window_ms AS DeliveryWindowMs,
               working_window_ms  AS WorkingWindowMs,
               grace_period_ms    AS GracePeriodMs,
               delivered_utc     AS DeliveredUtc,
               resolution        AS Resolution,
               opened_utc        AS OpenedUtc,
               expires_utc       AS ExpiresUtc,
               closed_utc        AS ClosedUtc,
               is_decision       AS IsDecision,
               decision_ref      AS DecisionRef,
               version           AS Version
        FROM exchanges
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Loads a single exchange by its identity.
    /// </summary>
    /// <param name="id">The exchange's primary key.</param>
    /// <param name="transaction">
    /// The transaction to read within, or <see langword="null"/> to read outside one. Pass the
    /// ambient transaction when the result will inform a write, so the row cannot shift between
    /// the decision and the write that acts on it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The exchange in whatever state it currently holds, or <see langword="null"/> if no
    /// row exists with that identity.
    /// </returns>
    public async Task<Exchange?> GetAsync(
        long id,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE id = @Id;",
                new { Id = id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>
    /// Loads every open exchange.
    /// </summary>
    /// <param name="transaction">
    /// The transaction to read within, or <see langword="null"/> to read outside one. Pass
    /// the ambient transaction when the result will inform a write, so the set cannot shift
    /// underneath the decision.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>All exchanges currently in the open state.</returns>
    /// <remarks>
    /// This is the input to <see cref="WaitForGraph.FromOpenExchanges"/>. The open set is
    /// bounded by the number of registered agents rather than by history, so loading it
    /// whole is cheap and stays cheap.
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetOpenAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE state = 'OPEN';",
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads open exchanges whose deadline has passed, for the reaper to orphan.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate deadlines against.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Open exchanges that expired at or before <paramref name="nowUtc"/>.</returns>
    /// <remarks>
    /// The comparison is a string comparison, correct only because timestamps are stored in
    /// round-trip format. See <see cref="ExchangeRow.FormatTimestamp"/>.
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetExpiredAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE state = 'OPEN' AND expires_utc <= @Now;",
                new { Now = ExchangeRow.FormatTimestamp(nowUtc) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads the open exchanges an agent owes the next move on and has already had its turn at.
    /// </summary>
    /// <param name="agent">The agent whose debts these are.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// Every open exchange whose <c>owed_by</c> is this agent and on which this agent has
    /// nothing left to collect.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The input to SPEC.md §14's attach sequence, and the reason it is <c>owed_by</c> rather
    /// than "party to". An exchange the agent is <em>waiting on</em> has somebody else working
    /// towards its deadline, and orphaning it would discard a peer's work in progress because a
    /// process on the other side of the conversation happened to restart. What has no worker is
    /// the debt the vanished agent was carrying.
    /// </para>
    /// <para>
    /// <b>And carrying it means having been given it.</b> A question asked while the recipient's
    /// session was closed is still sitting in that agent's inbox undelivered, so there is no
    /// vanished worker and nothing to release — the agent arriving <em>is</em> the worker,
    /// arriving. Without this condition the attach sequence orphans that question in the moment
    /// before its recipient could read it, and that is not an edge case: with one agent per
    /// repository most repositories are closed most of the time, so most cross-session questions
    /// are asked into a closed session, and every one of them would be destroyed by its
    /// recipient opening theirs. What the agent sees then is a question in its inbox and a
    /// refusal saying it was abandoned because nobody returned to it in time — on a question it
    /// returned to within seconds.
    /// </para>
    /// <para>
    /// So the test is what the agent still has pending, not what it has seen. Those differ in
    /// the middle of an exchange: an agent that read a question, asked for detail, and closed
    /// its session before the detail arrived has seen something on that exchange but has not
    /// seen the turn it now owes an answer to. Orphaning that would discard a peer's reply that
    /// nobody had read, which is the same defect one step further in.
    /// </para>
    /// <para>
    /// What remains, and what §14 means, is an exchange whose whole conversation has been
    /// collected and left unanswered: the agent saw it, the session ended, nobody carried on.
    /// Nothing is lost by waiting for delivery either, because an uncollected question is still
    /// bounded — §6's window expires it and the sweep orphans it then, which is the deadline
    /// doing its job rather than a restart standing in for one.
    /// </para>
    /// <para>
    /// The second condition is redundant against today's message flow, which always sends the
    /// owing agent something before the debt exists, and is kept because the costs are not
    /// symmetric. Orphaning an exchange the agent never saw destroys a question; declining to
    /// orphan one leaves it to expire on its own deadline a few minutes later.
    /// </para>
    /// <para>
    /// Distinct from <see cref="GetOrphanedOwedByAsync"/>, which reads the same debts once they
    /// have already been orphaned and is what a replay works from. This one is what turns the
    /// first into the second.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetAbandonedOwedByAsync(
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            new CommandDefinition(
                $"""
                {SelectColumns}
                WHERE state = 'OPEN'
                  AND owed_by = @Agent
                  AND NOT EXISTS (SELECT 1 FROM messages m
                                   WHERE m.exchange_id = exchanges.id
                                     AND m.to_agent = @Agent
                                     AND m.delivered_utc IS NULL)
                  AND EXISTS (SELECT 1 FROM messages m
                               WHERE m.exchange_id = exchanges.id
                                 AND m.to_agent = @Agent);
                """,
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads orphaned exchanges awaiting a response from a particular agent.
    /// </summary>
    /// <param name="agent">The agent that owes the outstanding response.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Orphaned exchanges owed by <paramref name="agent"/>.</returns>
    /// <remarks>
    /// <para>
    /// Called when an agent re-registers after a restart, so its pending questions can be
    /// resumed and replayed into its inbox instead of being lost. This works because
    /// exchanges are keyed on logical agent name rather than on a session identifier.
    /// Returning an exchange here does not authorise replaying it: whether a restart resumes
    /// anything at all is the operator's configured policy, per SPEC.md section 14.
    /// </para>
    /// <para>
    /// The result may contain several orphans sharing one pair, which is now ordinary rather
    /// than a hazard: a pair may hold several open exchanges provided they carry different
    /// questions. What still cannot be resumed is an orphan whose question is already open
    /// again — <c>ux_open_question</c> is partial over open rows, so an identical re-ask
    /// refuses the replay. That resolves the collision half of §17 open question 9; what
    /// remains open there is the incentive, since the surviving re-ask starts at clarify
    /// depth zero while the orphan held the accumulated depth.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetOrphanedOwedByAsync(
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE state = 'ORPHANED' AND owed_by = @Agent;",
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads orphans whose grace period has elapsed and which may therefore be ghosted.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate against.</param>
    /// <param name="brokerStartUtc">When the broker most recently started.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The orphans that may be closed as ghosted.</returns>
    /// <remarks>
    /// <para>
    /// The clock runs from the later of orphaning and broker start, which is why this cannot
    /// be a plain predicate on <c>expires_utc</c>. A broker that was down overnight returns to
    /// exchanges whose grace period elapsed while nothing could possibly have resumed them, and
    /// a wall-clock rule would ghost every one of them within a second of start-up.
    /// </para>
    /// <para>
    /// <c>Orphan</c> restamps <c>expires_utc</c> with the instant of orphaning, so that column
    /// carries the start of the grace period rather than the deadline it originally held.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetGhostableAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset brokerStartUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE state = 'ORPHANED';",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Filtered in memory rather than in SQL. The predicate mixes a stored timestamp, a
        // stored duration and a value only the host knows, and expressing that in SQL means
        // arithmetic on text timestamps that the domain already does correctly. The set is
        // bounded by the number of registered pairs rather than by history.
        return [.. rows.Select(r => r.ToDomain())
            .Where(e => e.IsGhostableAt(nowUtc, brokerStartUtc))];
    }

    /// <summary>
    /// Attempts to open an exchange, applying pair mutual exclusion and cycle rejection.
    /// </summary>
    /// <param name="asker">The agent posing the question.</param>
    /// <param name="answerer">The agent expected to answer it.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="questionHash">
    /// Content identity for the question, which the distinctness index refuses to duplicate
    /// while an identical question is still open on this pair.
    /// </param>
    /// <param name="conversationId">
    /// The discussion this exchange belongs to, or <see langword="null"/> if it stands alone.
    /// </param>
    /// <param name="policy">
    /// The terms to admit under, or <see langword="null"/> for
    /// <see cref="ExchangePolicy.Default"/>. Resolved, never named.
    /// </param>
    /// <param name="transaction">
    /// A transaction to enlist in, or <see langword="null"/> to own one. Supplied when this
    /// write is one step of a larger operation that must commit or fail whole; see
    /// <c>WriteScope</c>. A borrowed transaction is not committed here.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// An outcome carrying either the opened exchange, the debt the asker must settle first,
    /// the exchange it is already waiting on, or the cycle the question would have closed.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="asker"/> and <paramref name="answerer"/> are the same agent.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> carries a value outside what storage will accept.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The transaction is <c>IMMEDIATE</c>, not deferred. This matters: a deferred
    /// transaction acquires a read snapshot on the first <c>SELECT</c> and only upgrades to a
    /// write lock at the <c>INSERT</c>. If another connection commits in between, WAL mode
    /// refuses the upgrade with <c>SQLITE_BUSY_SNAPSHOT</c>, which <c>busy_timeout</c> cannot
    /// resolve because waiting never makes a stale snapshot fresh. Taking the write lock up
    /// front means the open set cannot shift between the read and the insert, which is what
    /// makes the checks below sound. The <c>ux_open_pair</c> unique index remains the
    /// backstop.
    /// </para>
    /// <para>
    /// Reachability is checked first, because no other check can express it: a deleted
    /// repository leaves its agent registered and its pair slot free, so admission would
    /// otherwise succeed and the question would sit until it expired.
    /// </para>
    /// <para>
    /// The pair check then runs before the cycle check deliberately. It is the more specific
    /// condition, and it is the only one that can return useful work rather than a bare
    /// refusal.
    /// </para>
    /// </remarks>
    public async Task<OpenOutcome> TryOpenAsync(
        AgentName asker,
        AgentName answerer,
        DateTimeOffset nowUtc,
        string questionHash,
        ExchangePolicy? policy = null,
        string? conversationId = null,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (asker == answerer)
        {
            throw new ArgumentException($"An agent cannot question itself ('{asker}').", nameof(answerer));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(questionHash);

        // A resolved policy, never the name of one. Selection belongs to the operator: the two
        // caps are controls rather than tuning knobs, and a control the controlled party
        // chooses is not a control. There is deliberately no parameter here an agent-facing
        // path could smuggle a policy name through.
        var terms = policy ?? ExchangePolicy.Default;
        terms.Validate();

        // Owns a transaction when called alone, joins one when composed into a larger
        // operation. See WriteScope for why an ask cannot be two commits.
        using var scope = WriteScope.Begin(_connection, transaction);

        // Checked before the pair and the cycle because it is the one condition none of them
        // can express. A dead repository leaves its agent registered and its pair slot free,
        // so every other check passes and the question is admitted, waits a full inactivity
        // window, and orphans unanswered — costing the same again on every retry while nobody
        // learns why.
        //
        // 'retired' counts for the same reason and was missing: SPEC.md §14 says a retired
        // agent is not routed to, and §10.1's agents() does not list it, so an asker should not
        // have the name at all — this is the backstop for one that does. Leaving it out made
        // retirement a control that does not hold, which is the exact failure §14 records when
        // it refuses to let re-registration clear the mark. The two statuses are checked
        // together rather than separately because the asker's correct response is identical:
        // nothing is coming from this peer, so tell your principal rather than trying later.
        //
        // Both are broker checks rather than triggers, unlike the cap and the distinctness
        // rule. Neither is an invariant: an agent may legitimately go unreachable or be retired
        // while it holds open exchanges, so a stored constraint would have to permit exactly
        // the rows it was meant to forbid.
        var agents = new AgentRepository(_connection);

        var unroutable = await agents
            .IsUnroutableAsync(answerer, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (unroutable)
        {
            return OpenOutcome.Unreachable();
        }

        // The same rule read from the other end. §14 says a retired agent is not routed to, and
        // an answer is a message addressed to the asker — so admitting a retired agent's
        // question schedules the violation rather than avoiding it. Only 'retired' counts here,
        // not 'unreachable': unreachable describes a peer nobody could spawn, which says
        // nothing about an agent that is plainly running well enough to be asking.
        var retired = await agents
            .IsRetiredAsync(asker, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (retired)
        {
            return OpenOutcome.AskerRetired();
        }

        // SPEC.md §5.0: a closed conversation stays closed, and the refusal says so. The
        // trigger refuses this too; checking here is what turns a constraint violation into a
        // refusal the asker can act on, exactly as the pair cap is checked above the index that
        // backs it.
        if (conversationId is not null)
        {
            var concluded = await _connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "SELECT count(*) FROM conversations WHERE id = @Id AND state = 'CLOSED';",
                    new { Id = conversationId },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (concluded > 0)
            {
                return OpenOutcome.ConversationClosed();
            }
        }

        var open = await GetOpenAsync(scope.Transaction, cancellationToken).ConfigureAwait(false);
        var pairKey = PairKey.For(asker, answerer);

        // Owing this peer an answer no longer blocks asking it something: a pair may carry
        // several open exchanges (SPEC.md §7.1). What used to be a refusal is now an ordering,
        // applied when the inbox is drained rather than when a question happens to collide.

        // Distinctness (SPEC.md §7.1). The unique index refuses this as well; finding it here
        // means the asker is handed the exchange already carrying its question instead of a
        // constraint violation. Repetition used to be structurally impossible because a pair
        // held one exchange, so removing that rule removed a guard nobody had written down.
        //
        // Read from storage rather than from the loaded exchanges, so question_hash stays out
        // of the domain model. It identifies a question for admission control and has no part
        // in the state machine, and Exchange is the state machine.
        var duplicateId = await _connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                """
                SELECT id FROM exchanges
                 WHERE state = 'OPEN' AND asker = @Asker AND answerer = @Answerer
                   AND question_hash = @QuestionHash
                 LIMIT 1;
                """,
                new { Asker = asker.Value, Answerer = answerer.Value, QuestionHash = questionHash },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var outstanding = open
            .Where(e => e.Asker == asker && e.Answerer == answerer)
            .ToList();

        if (duplicateId is { } existingId)
        {
            return OpenOutcome.Duplicate(outstanding.First(e => e.Id == existingId));
        }

        // The cap bounds volume, which distinctness does not: five different questions are
        // five context loads for the peer whether or not any of them repeats.
        if (outstanding.Count >= terms.OpenPairCap)
        {
            return OpenOutcome.PairSaturated(outstanding);
        }

        var graph = WaitForGraph.FromOpenExchanges(open);
        if (graph.WouldDeadlock(asker, answerer, out var cyclePath))
        {
            return OpenOutcome.Deadlock(cyclePath);
        }

        // Created here, inside the transaction that admits the question naming it, and
        // idempotently: two agents may name the same new identifier at once and neither should
        // lose. Agents never create conversations explicitly, because a conversation is not a
        // thing to be set up before use — it is whatever its participants said belonged
        // together (SPEC.md 5.0).
        if (conversationId is not null)
        {
            var conversations = new ConversationRepository(_connection);

            await conversations
                .EnsureOpenAsync(conversationId, nowUtc, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);

            // Membership and the void, both from SPEC.md §5.0 and both consequences of this
            // one act. An agent is a participant because it is party to an exchange carrying
            // the identifier, so recording that anywhere but here could disagree with the
            // exchange that established it. And opening an exchange discards any poll in
            // progress along with every vote cast in it, because a conversation that concluded
            // around a live question would leave that question in a discussion nobody may add
            // to.
            await conversations
                .JoinAsync(conversationId, asker, answerer, nowUtc, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        var expires = nowUtc + terms.DeliveryWindow;
        var id = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO exchanges
                    (pair_key, asker, answerer, owed_by, state, clarify_depth, clarify_depth_cap,
                     delivery_window_ms, working_window_ms, grace_period_ms,
                     opened_utc, expires_utc, question_hash, conversation_id, open_pair_cap,
                     version)
                VALUES
                    (@PairKey, @Asker, @Answerer, @Answerer, 'OPEN', 0, @Cap,
                     @DeliveryWindowMs, @WorkingWindowMs, @GracePeriodMs,
                     @OpenedUtc, @ExpiresUtc, @QuestionHash, @ConversationId, @OpenPairCap, 0)
                RETURNING id;
                """,
                new
                {
                    PairKey = pairKey.Value,
                    Asker = asker.Value,
                    Answerer = answerer.Value,
                    Cap = terms.ClarifyDepthCap,
                    OpenPairCap = terms.OpenPairCap,
                    DeliveryWindowMs = (long)terms.DeliveryWindow.TotalMilliseconds,
                    WorkingWindowMs = (long)terms.WorkingWindow.TotalMilliseconds,
                    GracePeriodMs = (long)terms.GracePeriod.TotalMilliseconds,
                    OpenedUtc = ExchangeRow.FormatTimestamp(nowUtc),
                    ExpiresUtc = ExchangeRow.FormatTimestamp(expires),
                    QuestionHash = questionHash,
                    ConversationId = conversationId
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();

        return OpenOutcome.Opened(
            Exchange.Rehydrate(
                id, asker, answerer, answerer, ExchangeState.Open,
                0, terms.ClarifyDepthCap, terms.DeliveryWindow, terms.WorkingWindow,
                terms.GracePeriod, nowUtc, expires, null, null, false, null));
    }

    /// <summary>
    /// Persists the mutable state of an exchange after a domain transition, rejecting the
    /// write if the stored row has changed since it was loaded.
    /// </summary>
    /// <param name="exchange">
    /// The exchange to save. Its immutable fields — participants, pair key, creation time —
    /// are not written.
    /// </param>
    /// <param name="decisionRef">
    /// A pointer to the promoted decision record, when the closing answer produced one.
    /// </param>
    /// <param name="transaction">
    /// A transaction to enlist in, or <see langword="null"/> to own one. Supplied when this
    /// write is one step of a larger operation that must commit or fail whole; see
    /// <c>WriteScope</c>. A borrowed transaction is not committed here.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the row was updated and this instance's
    /// <see cref="Exchange.Version"/> has been advanced; <see langword="false"/> if another
    /// writer changed the row first, or the row no longer exists.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">
    /// The write would put two open exchanges on one pair, violating <c>ux_open_pair</c>.
    /// Reachable when resuming: that index is partial over open rows, so a pair may hold
    /// several orphans at once, and only the first of them can be resumed. See
    /// <see cref="GetOrphanedOwedByAsync"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A pair conflict throws rather than returning <see langword="false"/>, because the two
    /// mean different things to a caller. <see langword="false"/> says "you are stale, reload
    /// and decide again"; a caller that did so here would reload, find the transition still
    /// legal, and retry forever, since nothing about reloading frees the slot another
    /// exchange holds.
    /// </para>
    /// <para>
    /// The update is guarded on <see cref="Exchange.Version"/>. A <see langword="false"/>
    /// return means this instance is stale and its transition was not applied; the caller
    /// must reload and decide again rather than retrying blindly, since the transition it
    /// intended may no longer be legal.
    /// </para>
    /// <para>
    /// Call this after every successful transition. An exchange closed in memory but not
    /// saved continues to hold its pair slot and its wait-for edge, since both are derived
    /// from stored state.
    /// </para>
    /// <para>
    /// Within a borrowed transaction, a <see langword="true"/> return means the row was
    /// updated, not that the update is durable — the caller may still roll back. The version
    /// stamp on <paramref name="exchange"/> has already advanced by then, so an instance whose
    /// save was rolled back is ahead of storage and every later save through it will report
    /// stale. Discard it and reload rather than reusing it, which is what a caller composing
    /// several writes should do with all of them after a failure.
    /// </para>
    /// </remarks>
    public async Task<bool> SaveAsync(
        Exchange exchange,
        string? decisionRef = null,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var nextVersion = exchange.Version + 1;

        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE exchanges
                   SET owed_by       = @OwedBy,
                       state         = @State,
                       clarify_depth = @ClarifyDepth,
                       expires_utc   = @ExpiresUtc,
                       closed_utc    = @ClosedUtc,
                       delivered_utc = @DeliveredUtc,
                       resolution    = @Resolution,
                       is_decision   = @IsDecision,
                       decision_ref  = COALESCE(@DecisionRef, decision_ref),
                       version       = @NextVersion
                 WHERE id = @Id AND version = @ExpectedVersion;
                """,
                new
                {
                    exchange.Id,
                    OwedBy = exchange.OwedBy.Value,
                    State = ExchangeRow.FormatState(exchange.State),
                    exchange.ClarifyDepth,
                    ExpiresUtc = ExchangeRow.FormatTimestamp(exchange.ExpiresUtc),
                    ClosedUtc = exchange.ClosedUtc is { } c ? ExchangeRow.FormatTimestamp(c) : null,
                    DeliveredUtc = exchange.DeliveredUtc is { } d ? ExchangeRow.FormatTimestamp(d) : null,
                    Resolution = ExchangeRow.FormatResolution(exchange.Resolution),
                    IsDecision = exchange.IsDecision ? 1 : 0,
                    DecisionRef = decisionRef,
                    ExpectedVersion = exchange.Version,
                    NextVersion = nextVersion
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected == 0)
        {
            return false;
        }

        exchange.MarkSaved(nextVersion);
        return true;
    }
}
