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
    /// <summary>
    /// The default limit on open exchanges from one agent to another.
    /// </summary>
    /// <remarks>
    /// Directional, so a busy <c>orders to billing</c> cannot exhaust what <c>billing to
    /// orders</c> may open: they are unrelated lines of enquiry, and coupling them would
    /// refuse one for the sins of the other. The number is a starting estimate, like the
    /// clarify depth cap, and is meant to be tuned against real traffic.
    /// </remarks>
    public const int DefaultOpenPairCap = 5;

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
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The exchange in whatever state it currently holds, or <see langword="null"/> if no
    /// row exists with that identity.
    /// </returns>
    public async Task<Exchange?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE id = @Id;",
                new { Id = id },
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
    /// <param name="deliveryWindow">The window allowed for the question to be picked up.</param>
    /// <param name="workingWindow">The window allowed for an answer once it has been.</param>
    /// <param name="gracePeriod">How long the resulting orphan is kept before it is ghosted.</param>
    /// <param name="questionHash">
    /// Content identity for the question, which the distinctness index refuses to duplicate
    /// while an identical question is still open on this pair.
    /// </param>
    /// <param name="conversationId">
    /// The discussion this exchange belongs to, or <see langword="null"/> if it stands alone.
    /// </param>
    /// <param name="openPairCap">
    /// The limit on open exchanges from this asker to this answerer. Directional, so the two
    /// flows between a pair are independent.
    /// </param>
    /// <param name="clarifyDepthCap">
    /// The clarification request limit. Defaults to
    /// <see cref="Exchange.DefaultClarifyDepthCap"/>.
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
    /// A window or the grace period is not positive, or <paramref name="clarifyDepthCap"/> is
    /// negative.
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
        TimeSpan deliveryWindow,
        TimeSpan workingWindow,
        TimeSpan gracePeriod,
        string questionHash,
        string? conversationId = null,
        int clarifyDepthCap = Exchange.DefaultClarifyDepthCap,
        int openPairCap = DefaultOpenPairCap,
        CancellationToken cancellationToken = default)
    {
        if (asker == answerer)
        {
            throw new ArgumentException($"An agent cannot question itself ('{asker}').", nameof(answerer));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(questionHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deliveryWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(workingWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gracePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(clarifyDepthCap);

        // Synchronous BeginTransaction, deliberately. The async overloads do not expose the
        // deferred flag, and getting IMMEDIATE is the whole point here. Beginning a
        // transaction is a fast local operation with no I/O to await.
        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        // Checked before the pair and the cycle because it is the one condition none of them
        // can express. A dead repository leaves its agent registered and its pair slot free,
        // so every other check passes and the question is admitted, waits a full inactivity
        // window, and orphans unanswered — costing the same again on every retry while nobody
        // learns why.
        var reachable = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM agents WHERE name = @Name AND status = 'unreachable';",
                new { Name = answerer.Value },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (reachable > 0)
        {
            return OpenOutcome.Unreachable();
        }

        var open = await GetOpenAsync(transaction, cancellationToken).ConfigureAwait(false);
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
                transaction,
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
        if (outstanding.Count >= openPairCap)
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
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO conversations (id, state, opened_utc)
                    VALUES (@Id, 'OPEN', @Opened)
                    ON CONFLICT (id) DO NOTHING;
                    """,
                    new { Id = conversationId, Opened = ExchangeRow.FormatTimestamp(nowUtc) },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var expires = nowUtc + deliveryWindow;
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
                    Cap = clarifyDepthCap,
                    OpenPairCap = openPairCap,
                    DeliveryWindowMs = (long)deliveryWindow.TotalMilliseconds,
                    WorkingWindowMs = (long)workingWindow.TotalMilliseconds,
                    GracePeriodMs = (long)gracePeriod.TotalMilliseconds,
                    OpenedUtc = ExchangeRow.FormatTimestamp(nowUtc),
                    ExpiresUtc = ExchangeRow.FormatTimestamp(expires),
                    QuestionHash = questionHash,
                    ConversationId = conversationId
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OpenOutcome.Opened(
            Exchange.Rehydrate(
                id, asker, answerer, answerer, ExchangeState.Open,
                0, clarifyDepthCap, deliveryWindow, workingWindow, gracePeriod,
                nowUtc, expires, null, null, false, null));
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
    /// </remarks>
    public async Task<bool> SaveAsync(
        Exchange exchange,
        string? decisionRef = null,
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
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected == 0)
        {
            return false;
        }

        exchange.MarkSaved(nextVersion);
        return true;
    }
}
