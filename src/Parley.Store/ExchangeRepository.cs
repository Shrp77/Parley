using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Reads and writes exchanges, and owns admission control for new ones.
/// </summary>
/// <remarks>
/// <para>
/// Admission lives here rather than on the aggregate because every rule in it needs to see more than
/// one exchange: which conversations a sender holds, whether the same Request is already open, how many
/// the sender has open with the receiver, and whether this one closes a cycle.
/// </para>
/// <para>
/// Instances are cheap and hold no state beyond the connection. They are not thread-safe, because a
/// <see cref="SqliteConnection"/> is not.
/// </para>
/// </remarks>
/// <param name="connection">An open connection to the Parley database, already migrated.</param>
public sealed class ExchangeRepository(SqliteConnection connection)
{
    private const string SelectColumns =
        """
        SELECT id                  AS Id,
               pair_key            AS PairKey,
               requester           AS Requester,
               receiver            AS Receiver,
               owed_by             AS OwedBy,
               state               AS State,
               clarify_depth       AS ClarifyDepth,
               clarify_depth_cap   AS ClarifyDepthCap,
               uncollected_flag_ms AS UncollectedFlagMs,
               stalled_flag_ms     AS StalledFlagMs,
               opened_utc          AS OpenedUtc,
               delivered_utc       AS DeliveredUtc,
               closed_utc          AS ClosedUtc,
               outcome             AS Outcome,
               version             AS Version
        FROM exchanges
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Loads a single exchange.</summary>
    /// <param name="id">The exchange's primary key.</param>
    /// <param name="transaction">The transaction to read within, when the result will inform a write.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The exchange, or <see langword="null"/> if none has that identity.</returns>
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

    /// <summary>Loads every open exchange.</summary>
    /// <param name="transaction">The transaction to read within, when the result will inform a write.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>All open exchanges.</returns>
    /// <remarks>
    /// The input to cycle detection. The open set is bounded by work in flight rather than by history,
    /// so loading it whole stays cheap.
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

    /// <summary>Loads the open exchanges in one conversation.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="transaction">The transaction to read within.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its open exchanges, oldest first.</returns>
    public async Task<IReadOnlyList<Exchange>> GetOpenInConversationAsync(
        string conversationId,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE state = 'OPEN' AND conversation_id = @Conversation ORDER BY id;",
                new { Conversation = conversationId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>Loads the open exchanges a participant owes the next move on.</summary>
    /// <param name="agent">The participant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every open exchange whose <c>owed_by</c> it is.</returns>
    /// <remarks>What a restart under <see cref="ResumePolicy.Always"/> wakes agents for (§14).</remarks>
    public async Task<IReadOnlyList<Exchange>> GetOwedByAsync(
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ExchangeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE state = 'OPEN' AND owed_by = @Agent ORDER BY id;",
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads the open exchanges an agent owes and has already collected everything on.
    /// </summary>
    /// <param name="agent">The agent attaching.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Exchanges whose messages to this agent were all delivered, and at least one was.</returns>
    /// <remarks>
    /// <para>
    /// What an agent coming back is given again (§6): work collected by a session that ended before
    /// sending its Result. Nothing about the exchange changes — it was open throughout — but the latest
    /// message the agent owes a move on is marked undelivered, so the next collection hands it over ahead
    /// of anything new.
    /// </para>
    /// <para>
    /// <b>Only what it had already collected.</b> A Request still sitting undelivered was never carried
    /// by anybody, so there is nothing to give again: the agent arriving is the worker, arriving, and the
    /// ordinary collection delivers it.
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

    /// <summary>How many open exchanges hold their next move with an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// Whether an agent owes anything right now, which is the broker's half of "idle" before the facilitator asks its
    /// node to refresh it (SPEC.md §11.1). The node's half — no turn in progress — only the node can see.
    /// </remarks>
    public async Task<int> CountOwedByAsync(AgentName agent, CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM exchanges WHERE state = 'OPEN' AND owed_by = @Agent;",
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>The conversations a participant holds a Request in: received and not yet answered with a Result.</summary>
    /// <param name="agent">The participant.</param>
    /// <param name="transaction">The transaction to read within.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The conversation identifiers, in order.</returns>
    /// <remarks>
    /// <para>
    /// The fact §5.0 assigns a conversation from. Held means received: an exchange the participant is
    /// waiting on as requester is a line of work somebody else is carrying, and what the participant does
    /// meanwhile is not part of it.
    /// </para>
    /// <para>
    /// <b>The facilitator's question is not held</b> (SPEC.md principle 2.10). Counted, an open question — and one
    /// asked back can stay open indefinitely — would pull the agent's own work into its <c>owns-</c> conversation,
    /// or make it name a conversation it would otherwise not have to: the facilitator deciding where work goes, which
    /// principle 2.11 forbids.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetHeldConversationsAsync(
        AgentName agent,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT DISTINCT conversation_id
                  FROM exchanges
                 WHERE state = 'OPEN' AND receiver = @Agent
                   AND requester NOT IN (SELECT name FROM agents WHERE adapter = @Facilitator)
                 ORDER BY conversation_id;
                """,
                new { Agent = agent.Value, Facilitator = Facilitation.Adapter },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Decides which conversation a Request goes into (§5.0).</summary>
    /// <param name="sender">The participant sending the Request.</param>
    /// <param name="named">The conversation the sender named, or <see langword="null"/>.</param>
    /// <param name="senderIsPerson">Whether the sender is a person taking part from a seat.</param>
    /// <param name="transaction">The transaction to read within.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The conversation, or the refusal with what the sender may name instead.</returns>
    /// <remarks>
    /// <para>
    /// <b>The broker decides, and the sender cannot split a line of work by naming something new.</b>
    /// Holding Requests from one conversation, a sender's Requests go into it and it names nothing.
    /// Holding them from several, it names one of those. Holding none, it may continue a conversation it
    /// is already in, or start one. A person may always start one (principle 2.10), because a person is
    /// where lines of work begin.
    /// </para>
    /// <para>
    /// A destroyed conversation's identifier is never assignable. §14 keeps it taken so a new line of
    /// work cannot appear under the name of one somebody already holds an export of.
    /// </para>
    /// </remarks>
    public async Task<ConversationAssignment> AssignConversationAsync(
        AgentName sender,
        string? named,
        bool senderIsPerson,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var held = senderIsPerson
            ? []
            : await GetHeldConversationsAsync(sender, transaction, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(named))
        {
            return held.Count switch
            {
                0 => new ConversationAssignment(NewConversationId(), Starts: true, Refusal: null),
                1 => new ConversationAssignment(held[0], Starts: false, Refusal: null),
                _ => new ConversationAssignment(null, false, OpenOutcome.NotAssignable(held))
            };
        }

        var conversations = new ConversationRepository(_connection);
        var existing = await conversations.GetAsync(named, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (existing?.State == ConversationState.Closed)
        {
            return new ConversationAssignment(null, false, OpenOutcome.ConversationClosed(existing.ClosedBy));
        }

        if (existing is null)
        {
            var destroyed = await _connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "SELECT count(*) FROM destroyed_conversations WHERE id = @Id;",
                    new { Id = named },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

            if (destroyed || held.Count > 0)
            {
                return new ConversationAssignment(null, false, OpenOutcome.NotAssignable(held));
            }

            return new ConversationAssignment(named, Starts: true, Refusal: null);
        }

        if (senderIsPerson || held.Contains(named, StringComparer.Ordinal))
        {
            return new ConversationAssignment(named, Starts: false, Refusal: null);
        }

        if (held.Count == 0
            && await conversations.IsParticipantAsync(named, sender, transaction, cancellationToken)
                .ConfigureAwait(false))
        {
            return new ConversationAssignment(named, Starts: false, Refusal: null);
        }

        return new ConversationAssignment(null, false, OpenOutcome.NotAssignable(held));
    }

    /// <summary>An identifier for a conversation its starter did not name (§5.0).</summary>
    /// <returns>A fresh identifier inside the character rules of §9.2.</returns>
    private static string NewConversationId() => "c-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>Tries to open an exchange in an assigned conversation.</summary>
    /// <param name="requester">The participant sending the Request.</param>
    /// <param name="receiver">The participant asked to do the work.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="requestHash">The Request's content identity (§7.1).</param>
    /// <param name="conversationId">The conversation <see cref="AssignConversationAsync"/> assigned.</param>
    /// <param name="policy">The terms to admit under, or <see langword="null"/> for the default. Resolved, never named.</param>
    /// <param name="requestGroup">The group binding the exchanges of one Request to a role (§5.5), or <see langword="null"/>.</param>
    /// <param name="transaction">A transaction to enlist in, or <see langword="null"/> to own one.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The opened exchange, with any cycle it closed; or the refusal.</returns>
    /// <exception cref="ArgumentException">The requester and receiver are the same participant.</exception>
    /// <remarks>
    /// <para>
    /// <b>The transaction is IMMEDIATE.</b> Every check here reads other rows before writing one, and
    /// every one is wrong if another writer commits in between (§9.3).
    /// </para>
    /// <para>
    /// <b>A cycle is admitted and recorded</b> (§7.2). Refusing one refused the communication that
    /// resolves blockers; recording it puts the loop in front of the person, where a loop that stalls is
    /// visible for what it is.
    /// </para>
    /// <para>
    /// Retirement is the only statement about a receiver that refuses a Request (§14). A receiver nobody
    /// is running is started by the Request, and one on a switched-off machine is flagged — refusing
    /// either at the door would refuse the traffic that fixes it.
    /// </para>
    /// </remarks>
    public async Task<OpenOutcome> TryOpenAsync(
        AgentName requester,
        AgentName receiver,
        DateTimeOffset nowUtc,
        string requestHash,
        string conversationId,
        ExchangePolicy? policy = null,
        string? requestGroup = null,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (requester == receiver)
        {
            throw new ArgumentException(
                $"A participant cannot send a request to itself ('{requester}').", nameof(receiver));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var terms = policy ?? ExchangePolicy.Default;
        terms.Validate();

        using var scope = WriteScope.Begin(_connection, transaction);

        var agents = new AgentRepository(_connection);

        if (!await agents.IsRegisteredAsync(receiver, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            return OpenOutcome.Refused(RequestRejection.ReceiverNotRegistered);
        }

        // Here rather than in the broker's admission alone, because every exchange is opened through this method —
        // including the facilitator's own, where it is the requester and never the receiver.
        if (string.Equals(
                await agents.AdapterOfAsync(receiver, scope.Transaction, cancellationToken).ConfigureAwait(false),
                Facilitation.Adapter,
                StringComparison.Ordinal))
        {
            return OpenOutcome.Refused(RequestRejection.ReceiverIsTheFacilitator);
        }

        if (await agents.IsRetiredAsync(receiver, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            return OpenOutcome.Refused(RequestRejection.ReceiverRetired);
        }

        if (await agents.IsRetiredAsync(requester, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            return OpenOutcome.Refused(RequestRejection.SenderRetired);
        }

        var conversations = new ConversationRepository(_connection);
        var conversation = await conversations.GetAsync(conversationId, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (conversation?.State == ConversationState.Closed)
        {
            return OpenOutcome.ConversationClosed(conversation.ClosedBy);
        }

        var open = await GetOpenAsync(scope.Transaction, cancellationToken).ConfigureAwait(false);

        var outstanding = open
            .Where(e => e.Requester == requester && e.Receiver == receiver)
            .ToList();

        // Read from storage rather than the loaded exchanges, so request_hash stays out of the domain
        // model: it identifies a Request for admission and has no part in the state machine.
        var duplicateId = await _connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                """
                SELECT id FROM exchanges
                 WHERE state = 'OPEN' AND requester = @Requester AND receiver = @Receiver
                   AND request_hash = @Hash
                 LIMIT 1;
                """,
                new { Requester = requester.Value, Receiver = receiver.Value, Hash = requestHash },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (duplicateId is { } existingId)
        {
            return OpenOutcome.Duplicate(outstanding.First(e => e.Id == existingId));
        }

        if (terms.BoundsRequests && outstanding.Count >= terms.OpenPairCap)
        {
            return OpenOutcome.Saturated(outstanding);
        }

        var closesCycle = WaitForGraph.FromOpenExchanges(open)
            .ClosesCycle(requester, receiver, out var cyclePath);

        // Created here, inside the transaction admitting the Request that starts it, and idempotently:
        // two senders may start the same named conversation at once and neither should fail.
        await conversations.EnsureOpenAsync(conversationId, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        // Membership and the void of any poll in progress, both consequences of this one act (§5.0).
        await conversations
            .JoinAsync(conversationId, requester, receiver, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        var id = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO exchanges
                    (pair_key, requester, receiver, owed_by, state, request_group, clarify_depth,
                     clarify_depth_cap, open_pair_cap, uncollected_flag_ms, stalled_flag_ms,
                     opened_utc, request_hash, conversation_id, version)
                VALUES
                    (@PairKey, @Requester, @Receiver, @Receiver, 'OPEN', @Group, 0,
                     @ClarifyCap, @PairCap, @UncollectedMs, @StalledMs,
                     @Opened, @Hash, @Conversation, 0)
                RETURNING id;
                """,
                new
                {
                    PairKey = PairKey.For(requester, receiver).Value,
                    Requester = requester.Value,
                    Receiver = receiver.Value,
                    Group = requestGroup,
                    ClarifyCap = terms.ClarifyDepthCap,
                    PairCap = terms.OpenPairCap,
                    UncollectedMs = (long)terms.UncollectedAfter.TotalMilliseconds,
                    StalledMs = (long)terms.StalledAfter.TotalMilliseconds,
                    Opened = ExchangeRow.FormatTimestamp(nowUtc),
                    Hash = requestHash,
                    Conversation = conversationId
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (closesCycle)
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO conversation_cycles (exchange_id, conversation_id, path, recorded_utc)
                    VALUES (@Exchange, @Conversation, @Path, @When);
                    """,
                    new
                    {
                        Exchange = id,
                        Conversation = conversationId,
                        Path = string.Join(CyclePathSeparator, cyclePath.Select(a => a.Value)),
                        When = ExchangeRow.FormatTimestamp(nowUtc)
                    },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        scope.Complete();

        return OpenOutcome.Opened(
            Exchange.Rehydrate(
                id, requester, receiver, receiver, ExchangeState.Open, 0, terms.ClarifyDepthCap,
                terms.UncollectedAfter, terms.StalledAfter, nowUtc, null, null, null),
            closesCycle ? cyclePath : []);
    }

    /// <summary>How a recorded cycle's participants are joined in <c>conversation_cycles.path</c>.</summary>
    /// <remarks>An arrow, because the path is read by a person, and no agent name can contain one.</remarks>
    public const string CyclePathSeparator = " -> ";

    /// <summary>
    /// Persists an exchange's mutable state after a transition, refusing the write if the row changed
    /// since it was loaded.
    /// </summary>
    /// <param name="exchange">The exchange to save.</param>
    /// <param name="transaction">A transaction to enlist in, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// <see langword="true"/> if the row was updated; <see langword="false"/> if another writer changed it
    /// first, in which case the caller must reload and decide again.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> is null.</exception>
    public async Task<bool> SaveAsync(
        Exchange exchange,
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
                       delivered_utc = @DeliveredUtc,
                       closed_utc    = @ClosedUtc,
                       outcome       = @Outcome,
                       version       = @NextVersion
                 WHERE id = @Id AND version = @ExpectedVersion;
                """,
                new
                {
                    exchange.Id,
                    OwedBy = exchange.OwedBy.Value,
                    State = ExchangeRow.FormatState(exchange.State),
                    exchange.ClarifyDepth,
                    DeliveredUtc = exchange.DeliveredUtc is { } d ? ExchangeRow.FormatTimestamp(d) : null,
                    ClosedUtc = exchange.ClosedUtc is { } c ? ExchangeRow.FormatTimestamp(c) : null,
                    Outcome = OutcomeTokens.Format(exchange.Outcome),
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

    /// <summary>The state of every exchange in one conversation, for the export of §10.2.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One snapshot per exchange, oldest first.</returns>
    public async Task<IReadOnlyList<ExchangeSnapshot>> GetSnapshotsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var rows = await _connection.QueryAsync<SnapshotRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, state AS State, opened_utc AS OpenedUtc, closed_utc AS ClosedUtc,
                       outcome AS Outcome
                  FROM exchanges
                 WHERE conversation_id = @Conversation
                 ORDER BY id;
                """,
                new { Conversation = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new ExchangeSnapshot(
            r.Id,
            ExchangeRow.ParseState(r.State),
            ExchangeRow.ParseTimestamp(r.OpenedUtc),
            r.ClosedUtc is null ? null : ExchangeRow.ParseTimestamp(r.ClosedUtc),
            OutcomeTokens.ParseExchange(r.Outcome)))];
    }

    private sealed class SnapshotRow
    {
        public long Id { get; init; }

        public string State { get; init; } = string.Empty;

        public string OpenedUtc { get; init; } = string.Empty;

        public string? ClosedUtc { get; init; }

        public string? Outcome { get; init; }
    }
}
