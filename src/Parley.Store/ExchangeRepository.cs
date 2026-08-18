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
               ttl_ms            AS TtlMs,
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
    /// The result may contain several orphans sharing one pair, because <c>ux_open_pair</c>
    /// is partial over open rows and a pair can be orphaned, reopened, and orphaned again.
    /// At most one of them can be resumed; saving a second throws. A caller replaying this
    /// list must therefore choose per pair rather than resuming all of it, and deciding
    /// which one wins is broker policy that does not exist yet.
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
    /// Attempts to open an exchange, applying pair mutual exclusion and cycle rejection.
    /// </summary>
    /// <param name="asker">The agent posing the question.</param>
    /// <param name="answerer">The agent expected to answer it.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="ttl">The inactivity window before the exchange may be orphaned.</param>
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
    /// <paramref name="ttl"/> is not positive, or <paramref name="clarifyDepthCap"/> is
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
    /// The pair check runs before the cycle check deliberately. It is the more specific
    /// condition, and it is the only one that can return useful work rather than a bare
    /// refusal.
    /// </para>
    /// </remarks>
    public async Task<OpenOutcome> TryOpenAsync(
        AgentName asker,
        AgentName answerer,
        DateTimeOffset nowUtc,
        TimeSpan ttl,
        int clarifyDepthCap = Exchange.DefaultClarifyDepthCap,
        CancellationToken cancellationToken = default)
    {
        if (asker == answerer)
        {
            throw new ArgumentException($"An agent cannot question itself ('{asker}').", nameof(answerer));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(clarifyDepthCap);

        // Synchronous BeginTransaction, deliberately. The async overloads do not expose the
        // deferred flag, and getting IMMEDIATE is the whole point here. Beginning a
        // transaction is a fast local operation with no I/O to await.
        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var open = await GetOpenAsync(transaction, cancellationToken).ConfigureAwait(false);
        var pairKey = PairKey.For(asker, answerer);

        var existing = open.FirstOrDefault(e => e.PairKey == pairKey);
        if (existing is not null)
        {
            return existing.OwedBy == asker
                ? OpenOutcome.OweFirst(existing)
                : OpenOutcome.AlreadyWaiting(existing);
        }

        var graph = WaitForGraph.FromOpenExchanges(open);
        if (graph.WouldDeadlock(asker, answerer, out var cyclePath))
        {
            return OpenOutcome.Deadlock(cyclePath);
        }

        var expires = nowUtc + ttl;
        var id = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO exchanges
                    (pair_key, asker, answerer, owed_by, state, clarify_depth, clarify_depth_cap,
                     ttl_ms, opened_utc, expires_utc, version)
                VALUES
                    (@PairKey, @Asker, @Answerer, @Answerer, 'OPEN', 0, @Cap,
                     @TtlMs, @OpenedUtc, @ExpiresUtc, 0)
                RETURNING id;
                """,
                new
                {
                    PairKey = pairKey.Value,
                    Asker = asker.Value,
                    Answerer = answerer.Value,
                    Cap = clarifyDepthCap,
                    TtlMs = (long)ttl.TotalMilliseconds,
                    OpenedUtc = ExchangeRow.FormatTimestamp(nowUtc),
                    ExpiresUtc = ExchangeRow.FormatTimestamp(expires)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OpenOutcome.Opened(
            Exchange.Rehydrate(
                id, asker, answerer, answerer, ExchangeState.Open,
                0, clarifyDepthCap, ttl, nowUtc, expires, null, false));
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
