using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Appends messages, deduplicates them, and drains agent inboxes.
/// </summary>
/// <remarks>
/// <para>
/// Instances are cheap and hold no state beyond the supplied connection. They are not
/// thread-safe, because a <see cref="SqliteConnection"/> is not; give each unit of work its
/// own connection and repository.
/// </para>
/// <para>
/// This repository owns the deduplication identity rather than accepting one. A caller that
/// supplied its own hash could supply a wrong one, and the schema cannot recompute it to
/// check — unlike <c>dedup_bucket</c>, which it derives. Computing here is what keeps
/// SPEC.md §13.1 a rule rather than a request.
/// </para>
/// </remarks>
/// <param name="connection">
/// An open connection to the Parley database, already migrated by
/// <see cref="SchemaMigrator"/>.
/// </param>
public sealed class MessageRepository(SqliteConnection connection)
{
    private const string SelectColumns =
        """
        SELECT id              AS Id,
               exchange_id     AS ExchangeId,
               type            AS Type,
               from_agent      AS FromAgent,
               to_agent        AS ToAgent,
               responder_model AS ResponderModel,
               payload_json    AS PayloadJson,
               conversation_id AS ConversationId,
               created_utc     AS CreatedUtc,
               delivered_utc   AS DeliveredUtc
        FROM messages
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));


    /// <summary>
    /// Appends a message unless an identical one was written inside the deduplication window.
    /// </summary>
    /// <param name="exchangeId">
    /// The exchange this belongs to, or <see langword="null"/> for a
    /// <see cref="MessageType.Notify"/>.
    /// </param>
    /// <param name="type">The message kind.</param>
    /// <param name="from">The sending agent.</param>
    /// <param name="to">The receiving agent.</param>
    /// <param name="responderModel">
    /// What produced this message. Required on every message, not only answers; see
    /// SPEC.md §11.3.
    /// </param>
    /// <param name="payloadJson">The payload document.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="conversationId">
    /// The discussion this message belongs to, or <see langword="null"/> for an exchange that
    /// stands alone. Supplied by the agent on a question or a notification; a clarification or
    /// answer inherits the identifier of the exchange it belongs to. See SPEC.md §5.0.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// An outcome carrying either the appended message or the earlier one that made this a
    /// duplicate.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="responderModel"/> or <paramref name="payloadJson"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="from"/> and <paramref name="to"/> are the same agent, or
    /// <paramref name="responderModel"/> or <paramref name="payloadJson"/> is empty.
    /// </exception>
    /// <exception cref="SqliteException">
    /// The row violates a schema constraint — an over-length payload, a payload that is not
    /// valid JSON, a notification carrying an exchange reference, or a responder identity
    /// outside the permitted character set.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The transaction is <c>IMMEDIATE</c>, for the reason set out on
    /// <see cref="ExchangeRepository.TryOpenAsync"/>: a deferred transaction takes its read
    /// snapshot at the first <c>SELECT</c> and only upgrades at the <c>INSERT</c>, and WAL
    /// refuses that upgrade if another connection committed in between. Taking the write lock
    /// up front is what makes the duplicate check sound rather than advisory.
    /// </para>
    /// <para>
    /// The conversation identifier is deliberately not part of the identity. It groups
    /// messages, it is not one of them, and hashing it would make the same text count as two
    /// different messages depending on which discussion an agent said it belonged to — so an
    /// agent could defeat deduplication simply by varying the identifier.
    /// </para>
    /// <para>
    /// The check here is the exact window; the unique index on
    /// <c>(content_hash, dedup_bucket)</c> is the backstop and is deliberately coarser. Two
    /// identical messages twenty seconds apart but either side of a bucket boundary pass the
    /// index and are refused here. The index exists for the writer that never ran this check.
    /// </para>
    /// </remarks>
    public async Task<AppendOutcome> AppendAsync(
        long? exchangeId,
        MessageType type,
        AgentName from,
        AgentName to,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        if (from == to)
        {
            throw new ArgumentException($"An agent cannot message itself ('{from}').", nameof(to));
        }

        ArgumentException.ThrowIfNullOrEmpty(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        var hash = MessageIdentity.Compute(exchangeId, type, from, to, payloadJson);
        var cutoff = ExchangeRow.FormatTimestamp(nowUtc - MessageIdentity.DeduplicationWindow);

        // Synchronous BeginTransaction, deliberately: the async overloads do not expose the
        // deferred flag, and IMMEDIATE is the whole point.
        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var existing = await _connection.QuerySingleOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                $"""
                {SelectColumns}
                WHERE content_hash = @Hash AND created_utc > @Cutoff
                ORDER BY id
                LIMIT 1;
                """,
                new { Hash = hash, Cutoff = cutoff },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (existing is not null)
        {
            return AppendOutcome.Duplicate(existing.ToDomain());
        }

        // The exchange holds the authoritative conversation (SPEC.md 5.0); a message attached
        // to one carries a copy, and the composite foreign key on messages keeps the two from
        // ever disagreeing. So this reads the exchange rather than reconstructing the answer
        // from whichever message happened to arrive first, which is what it used to do.
        //
        // A Notify has no exchange and therefore declares its own.
        var conversation = conversationId;

        if (exchangeId is { } exchange)
        {
            var owned = await _connection.ExecuteScalarAsync<string>(
                new CommandDefinition(
                    """
                    SELECT conversation_id FROM exchanges WHERE id = @Exchange;
                    """,
                    new { Exchange = exchange },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (conversationId is not null && conversationId != owned)
            {
                throw new ArgumentException(
                    owned is null
                        ? $"Exchange {exchange} belongs to no conversation, and a message cannot " +
                          $"enrol it in '{conversationId}'. The conversation is chosen when the " +
                          "exchange is opened."
                        : $"Exchange {exchange} belongs to conversation '{owned}'. A message on " +
                          $"it cannot declare '{conversationId}'.",
                    nameof(conversationId));
            }

            conversation = owned;
        }

        var created = ExchangeRow.FormatTimestamp(nowUtc);

        var id = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO messages
                    (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                     content_hash, conversation_id, created_utc)
                VALUES
                    (@ExchangeId, @Type, @From, @To, @Responder, @Payload, @Hash, @Conversation, @Created)
                RETURNING id;
                """,
                new
                {
                    ExchangeId = exchangeId,
                    Type = MessageIdentity.FormatType(type),
                    From = from.Value,
                    To = to.Value,
                    Responder = responderModel,
                    Payload = payloadJson,
                    Hash = hash,
                    Conversation = conversation,
                    Created = created
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return AppendOutcome.Appended(new Message(
            id, exchangeId, type, from, to, responderModel, payloadJson, conversation, nowUtc,
            null));
    }

    /// <summary>
    /// Loads the messages awaiting delivery to an agent, oldest first.
    /// </summary>
    /// <param name="agent">The recipient.</param>
    /// <param name="limit">
    /// The most messages to return, oldest first, or <see langword="null"/> for all of them.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Pending messages in the order they were appended.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    /// <remarks>
    /// <para>
    /// Ordered by identity rather than timestamp. Two messages can share a timestamp at the
    /// resolution stored, and an inbox that reordered them would present an answer before the
    /// clarification it answers. The <c>ix_inbox</c> index is on <c>(to_agent, id)</c> for
    /// this reason.
    /// </para>
    /// <para>
    /// <strong>Callers delivering into an agent's context should pass a limit.</strong> The
    /// 2,000-character cap bounds one message; nothing bounds a backlog. An agent offline for
    /// a day, or one peer sending steadily, produces an inbox whose total size is limited only
    /// by how long it went undrained — and injecting that wholesale recreates the
    /// context-poisoning problem the per-message cap exists to prevent, just in aggregate.
    /// SPEC.md §13 names per-sender rate limits as the control for this, and they are not
    /// implemented yet, so until they are the limit here is the only thing standing in the
    /// way.
    /// </para>
    /// <para>
    /// The default is unbounded because the repository does not know what the caller intends.
    /// A reaper or an audit wants everything; a delivery path wants a page. Truncating by
    /// default would silently hide messages from the callers that need them all.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// Debts first, then by age. SPEC.md §7.3 requires that an agent draining its inbox meets
    /// the questions it has not answered ahead of anything else — the adults-with-manners
    /// ordering, which exploits the fact that a pending question is frequently reshaped or made
    /// irrelevant by the answer to one already owed.
    /// </para>
    /// <para>
    /// Ordered in the query rather than by the caller, and there is deliberately no unordered
    /// overload. The ordering is a protocol rule, and a rule with a convenient way around it is
    /// a suggestion — the caller that reached for the unordered version would be the one under
    /// time pressure, which is exactly when the ordering earns its keep.
    /// </para>
    /// <para>
    /// A debt is a message whose exchange this agent owes a response on. An answer to its own
    /// question is not a debt and sorts after, and a Notify has no exchange at all so it sorts
    /// with them.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Message>> GetPendingAsync(
        AgentName agent,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is { } max)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(max, 0);
        }

        var rows = await _connection.QueryAsync<MessageRow>(
            new CommandDefinition(
                $"""
                {SelectColumns}
                WHERE to_agent = @Agent AND delivered_utc IS NULL
                ORDER BY CASE
                             WHEN EXISTS (SELECT 1 FROM exchanges e
                                           WHERE e.id = messages.exchange_id
                                             AND e.owed_by = @Agent)
                             THEN 0 ELSE 1
                         END,
                         id
                LIMIT @Limit;
                """,
                new { Agent = agent.Value, Limit = limit ?? -1 },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads every message in a conversation, oldest first.
    /// </summary>
    /// <param name="conversationId">The discussion to gather.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The conversation's messages in the order they were appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conversationId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// This is the export in SPEC.md §10.2, and it is an operator operation. It reads across
    /// agents by design, which is precisely why it must never be surfaced into an agent's tool
    /// set: it is the one query that would breach the isolation §10.1 exists to enforce.
    /// </para>
    /// <para>
    /// Deliberately unbounded, unlike the inbox drain. An export that silently truncated would
    /// present a partial record as a complete one, and the caller is an operator reading their
    /// own data rather than a context window with a budget.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Message>> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var rows = await _connection.QueryAsync<MessageRow>(
            new CommandDefinition(
                $"""
                {SelectColumns}
                WHERE conversation_id = @Conversation
                ORDER BY id;
                """,
                new { Conversation = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Loads a single message by its identity.
    /// </summary>
    /// <param name="id">The message's primary key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The message, or <see langword="null"/> if no row exists with that identity.</returns>
    public async Task<Message?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>
    /// Records that a message has been handed to its recipient.
    /// </summary>
    /// <param name="id">The message to mark.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if a pending message was marked; <see langword="false"/> if it
    /// does not exist or was already delivered.
    /// </returns>
    /// <remarks>
    /// Guarded on the message still being pending, so a concurrent second drain of the same
    /// inbox cannot overwrite the first delivery's timestamp with its own.
    /// </remarks>
    public async Task<bool> MarkDeliveredAsync(
        long id,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE messages
                   SET delivered_utc = @Now
                 WHERE id = @Id AND delivered_utc IS NULL;
                """,
                new { Id = id, Now = ExchangeRow.FormatTimestamp(nowUtc) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// Returns a delivered message to its recipient's inbox.
    /// </summary>
    /// <param name="id">The message to replay.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the message was returned to the inbox; <see langword="false"/>
    /// if no row exists with that identity.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replay is re-delivery, not re-sending, per SPEC.md §14. Writing a fresh row would make
    /// the log a record of how many times delivery was attempted rather than of what was said,
    /// which would distort both the per-exchange caps and the telemetry. A replay landing
    /// inside the deduplication window would additionally be refused outright, so on a fast
    /// restart the replay path would defeat itself.
    /// </para>
    /// <para>
    /// Whether a restart replays anything at all is the operator's configured policy, not this
    /// method's decision. See SPEC.md §14: killing the broker to stop two agents consuming a
    /// quota and losing it to a forced update are indistinguishable from here.
    /// </para>
    /// </remarks>
    public async Task<bool> RedeliverAsync(long id, CancellationToken cancellationToken = default)
    {
        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE messages SET delivered_utc = NULL WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }
}
