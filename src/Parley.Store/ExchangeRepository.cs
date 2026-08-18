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
               ttl_seconds       AS TtlSeconds,
               opened_utc        AS OpenedUtc,
               expires_utc       AS ExpiresUtc,
               closed_utc        AS ClosedUtc,
               is_decision       AS IsDecision,
               decision_ref      AS DecisionRef
        FROM exchanges
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Loads a single exchange by its identity.
    /// </summary>
    /// <param name="id">The exchange's primary key.</param>
    /// <returns>
    /// The exchange in whatever state it currently holds, or <see langword="null"/> if no
    /// row exists with that identity.
    /// </returns>
    public async Task<Exchange?> GetAsync(long id)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<ExchangeRow>(
            $"{SelectColumns} WHERE id = @Id;",
            new { Id = id }).ConfigureAwait(false);

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
    /// <returns>All exchanges currently in the open state.</returns>
    /// <remarks>
    /// This is the input to <see cref="WaitForGraph.FromOpenExchanges"/>. The open set is
    /// bounded by the number of registered agents rather than by history, so loading it
    /// whole is cheap and stays cheap.
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetOpenAsync(SqliteTransaction? transaction = null)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            $"{SelectColumns} WHERE state = 'OPEN';",
            transaction: transaction).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads open exchanges whose deadline has passed, for the reaper to orphan.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate deadlines against.</param>
    /// <returns>Open exchanges that expired at or before <paramref name="nowUtc"/>.</returns>
    /// <remarks>
    /// The comparison is a string comparison, correct only because timestamps are stored in
    /// round-trip format. See <see cref="ExchangeRow.FormatTimestamp"/>.
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetExpiredAsync(DateTimeOffset nowUtc)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            $"{SelectColumns} WHERE state = 'OPEN' AND expires_utc <= @Now;",
            new { Now = ExchangeRow.FormatTimestamp(nowUtc) }).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads orphaned exchanges awaiting a response from a particular agent.
    /// </summary>
    /// <param name="agent">The agent that owes the outstanding response.</param>
    /// <returns>Orphaned exchanges owed by <paramref name="agent"/>.</returns>
    /// <remarks>
    /// Called when an agent re-registers after a restart, so its pending questions can be
    /// resumed and replayed into its inbox instead of being lost. This works because
    /// exchanges are keyed on logical agent name rather than on a session identifier.
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> GetOrphanedOwedByAsync(AgentName agent)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            $"{SelectColumns} WHERE state = 'ORPHANED' AND owed_by = @Agent;",
            new { Agent = agent.Value }).ConfigureAwait(false);

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
    /// <returns>
    /// An outcome carrying either the opened exchange, the debt the asker must settle first,
    /// the exchange it is already waiting on, or the cycle the question would have closed.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="asker"/> and <paramref name="answerer"/> are the same agent.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Both checks run inside a single transaction. SQLite serialises writers, so the read of
    /// the open set cannot interleave with a competing opener's insert; the
    /// <c>ux_open_pair</c> unique index is the backstop rather than the primary mechanism.
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
        int clarifyDepthCap = Exchange.DefaultClarifyDepthCap)
    {
        if (asker == answerer)
        {
            throw new ArgumentException($"An agent cannot question itself ('{asker}').", nameof(answerer));
        }

        using var transaction = (SqliteTransaction)await _connection
            .BeginTransactionAsync().ConfigureAwait(false);

        var open = await GetOpenAsync(transaction).ConfigureAwait(false);
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
            """
            INSERT INTO exchanges
                (pair_key, asker, answerer, owed_by, state, clarify_depth, clarify_depth_cap,
                 ttl_seconds, opened_utc, expires_utc)
            VALUES
                (@PairKey, @Asker, @Answerer, @Answerer, 'OPEN', 0, @Cap,
                 @TtlSeconds, @OpenedUtc, @ExpiresUtc)
            RETURNING id;
            """,
            new
            {
                PairKey = pairKey.Value,
                Asker = asker.Value,
                Answerer = answerer.Value,
                Cap = clarifyDepthCap,
                TtlSeconds = (long)ttl.TotalSeconds,
                OpenedUtc = ExchangeRow.FormatTimestamp(nowUtc),
                ExpiresUtc = ExchangeRow.FormatTimestamp(expires)
            },
            transaction).ConfigureAwait(false);

        await transaction.CommitAsync().ConfigureAwait(false);

        return OpenOutcome.Opened(
            Exchange.Rehydrate(
                id, asker, answerer, answerer, ExchangeState.Open,
                0, clarifyDepthCap, ttl, nowUtc, expires, null, false));
    }

    /// <summary>
    /// Persists the mutable state of an exchange after a domain transition.
    /// </summary>
    /// <param name="exchange">
    /// The exchange to save. Its immutable fields — participants, pair key, creation time —
    /// are not written.
    /// </param>
    /// <param name="decisionRef">
    /// A pointer to the promoted decision record, when the closing answer produced one.
    /// Ignored unless the exchange is flagged as a decision.
    /// </param>
    /// <returns>The number of rows updated: one on success, zero if the row is missing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Call this after every successful transition. An exchange closed in memory but not
    /// saved continues to hold its pair slot and its wait-for edge, since both are derived
    /// from stored state.
    /// </remarks>
    public async Task<int> SaveAsync(Exchange exchange, string? decisionRef = null)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        return await _connection.ExecuteAsync(
            """
            UPDATE exchanges
               SET owed_by       = @OwedBy,
                   state         = @State,
                   clarify_depth = @ClarifyDepth,
                   expires_utc   = @ExpiresUtc,
                   closed_utc    = @ClosedUtc,
                   is_decision   = @IsDecision,
                   decision_ref  = @DecisionRef
             WHERE id = @Id;
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
                DecisionRef = decisionRef
            }).ConfigureAwait(false);
    }
}
