using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Appends messages, deduplicates them, and drains inboxes.
/// </summary>
/// <remarks>
/// <para>
/// Instances are cheap and hold no state beyond the connection, and are not thread-safe because a
/// <see cref="SqliteConnection"/> is not.
/// </para>
/// <para>
/// This repository computes the deduplication identity rather than accepting one. A caller that
/// supplied its own hash could supply a wrong one, and the schema cannot recompute it to check.
/// </para>
/// </remarks>
/// <param name="connection">An open connection to the Parley database, already migrated.</param>
public sealed class MessageRepository(SqliteConnection connection)
{
    /// <summary>What counts as waiting for a participant, shared by the drain and the count of it.</summary>
    /// <remarks>
    /// <para>
    /// Shared because the transport pushes a count of what is waiting and the adapter then drains to find
    /// out what it is. A count from a looser predicate than the drain uses reports work the drain will not
    /// hand over.
    /// </para>
    /// <para>
    /// <b>A Request is not delivered once its exchange has ended</b> (§14). It asks for work, and handing
    /// one over after its exchange closed spends a turn on work somebody finished or the person ended —
    /// and after a close, gives the receiver the Request at the moment the requester is told the work was
    /// ended. A Result closes its exchange and a Clarify is something said inside one the recipient is
    /// party to, so neither requires the exchange to be open.
    /// </para>
    /// </remarks>
    private const string PendingPredicate =
        """
        WHERE to_agent = @Agent AND delivered_utc IS NULL
          AND (type <> 'request'
               OR EXISTS (SELECT 1 FROM exchanges e
                           WHERE e.id = messages.exchange_id AND e.state = 'OPEN'))
        """;

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

    /// <summary>Appends a message unless an identical one was written inside the deduplication window.</summary>
    /// <param name="exchangeId">The exchange it belongs to.</param>
    /// <param name="type">The message kind.</param>
    /// <param name="from">The sender.</param>
    /// <param name="to">The recipient.</param>
    /// <param name="responderModel">What produced it (§11.3).</param>
    /// <param name="payloadJson">The payload document.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to enlist in, or <see langword="null"/> to own one.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The appended message, or the earlier one that made this a duplicate.</returns>
    /// <exception cref="ArgumentException">
    /// The sender and recipient are the same, or <paramref name="responderModel"/> or
    /// <paramref name="payloadJson"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">No such exchange exists.</exception>
    /// <exception cref="SqliteException">The row violates a schema constraint.</exception>
    /// <remarks>
    /// <para>
    /// The conversation is read from the exchange and never supplied. The broker assigned it when the
    /// exchange was admitted (§5.0), and the composite foreign key would refuse a different one anyway —
    /// there is no reason to offer a caller the chance to try.
    /// </para>
    /// <para>
    /// The check here is the exact window; the unique index on <c>(content_hash, dedup_bucket)</c> is the
    /// coarser backstop for a writer that never ran it.
    /// </para>
    /// </remarks>
    public async Task<AppendOutcome> AppendAsync(
        long exchangeId,
        MessageType type,
        AgentName from,
        AgentName to,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (from == to)
        {
            throw new ArgumentException($"A participant cannot message itself ('{from}').", nameof(to));
        }

        ArgumentException.ThrowIfNullOrEmpty(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        var cutoff = ExchangeRow.FormatTimestamp(nowUtc - MessageIdentity.DeduplicationWindow);

        using var scope = WriteScope.Begin(_connection, transaction);

        // Read before hashing: the exchange's version is part of the identity (§13.1), so the same words
        // at a later point in the exchange are a new message rather than a retry of an earlier one.
        var place = (await _connection.QueryAsync<(string ConversationId, long Version)>(
                new CommandDefinition(
                    "SELECT conversation_id, version FROM exchanges WHERE id = @Exchange;",
                    new { Exchange = exchangeId },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToList();

        if (place.Count == 0)
        {
            throw new InvalidOperationException(
                $"Exchange {exchangeId} does not exist, and every message belongs to one.");
        }

        var (conversation, version) = place[0];
        var hash = MessageIdentity.Compute(exchangeId, version, type, from, to, payloadJson);

        var existing = await _connection.QuerySingleOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                $"""
                {SelectColumns}
                WHERE content_hash = @Hash AND created_utc > @Cutoff
                ORDER BY id
                LIMIT 1;
                """,
                new { Hash = hash, Cutoff = cutoff },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (existing is not null)
        {
            return AppendOutcome.Duplicate(existing.ToDomain());
        }

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
                    Created = ExchangeRow.FormatTimestamp(nowUtc)
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();

        return AppendOutcome.Appended(new Message(
            id, exchangeId, type, from, to, responderModel, payloadJson, conversation, nowUtc, null));
    }

    /// <summary>The open exchanges a participant owes a move on.</summary>
    /// <param name="agent">The participant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Their identifiers.</returns>
    /// <remarks>
    /// So a collection can tell its debts from the rest, which <see cref="GetPendingAsync"/> orders by and does not say:
    /// a collection keeps room for the oldest message that is not a debt (SPEC.md §7.3).
    /// </remarks>
    public async Task<IReadOnlySet<long>> GetOwedExchangeIdsAsync(
        AgentName agent,
        CancellationToken cancellationToken = default) =>
        (await _connection.QueryAsync<long>(
            new CommandDefinition(
                "SELECT id FROM exchanges WHERE state = 'OPEN' AND owed_by = @Agent;",
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet();

    /// <summary>How many messages are waiting for a participant.</summary>
    /// <param name="agent">The participant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number <see cref="GetPendingAsync"/> would hand over, unlimited.</returns>
    /// <remarks>Reads and changes nothing, so it is safe to call from something that will not act on it.</remarks>
    public async Task<int> CountPendingAsync(
        AgentName agent,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"""
                SELECT count(*) FROM messages
                {PendingPredicate};
                """,
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>Loads the messages awaiting delivery to a participant, debts first.</summary>
    /// <param name="agent">The recipient.</param>
    /// <param name="limit">The most messages to return, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// Pending messages: those on exchanges it owes a move on first, taken in turn across senders, then the rest by age.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    /// <remarks>
    /// <para>
    /// <b>Debts first</b> (§7.3). A participant collecting its messages meets the work it owes ahead of
    /// anything new, on every collection, because a design whose correct behaviour depends on a model
    /// choosing to come back to something fails on turn forty.
    /// </para>
    /// <para>
    /// Ordered in the query and with no unordered overload. The ordering is a protocol rule, and a rule
    /// with a convenient way around it is a suggestion.
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

        // Each sender's debts are numbered in its own order, and debts are taken by that number: every sender's first,
        // then every sender's second. A collection is bounded (SPEC.md §10.1), so in plain age order one sender writing
        // fast enough held every other sender's Requests out of every collection.
        var rows = await _connection.QueryAsync<MessageRow>(
            new CommandDefinition(
                $"""
                WITH waiting AS (
                    SELECT id AS message_id,
                           from_agent AS sender,
                           CASE
                               WHEN EXISTS (SELECT 1 FROM exchanges e
                                             WHERE e.id = messages.exchange_id
                                               AND e.state = 'OPEN'
                                               AND e.owed_by = @Agent)
                               THEN 0 ELSE 1
                           END AS owed_rank
                    FROM messages
                    {PendingPredicate}),
                taken AS (
                    SELECT message_id,
                           owed_rank,
                           CASE owed_rank
                               WHEN 0 THEN ROW_NUMBER() OVER (PARTITION BY owed_rank, sender ORDER BY message_id)
                               ELSE 0
                           END AS turn
                    FROM waiting)
                {SelectColumns}
                JOIN taken ON taken.message_id = messages.id
                ORDER BY taken.owed_rank, taken.turn, messages.id
                LIMIT @Limit;
                """,
                new { Agent = agent.Value, Limit = limit ?? -1 },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>Loads every message in a conversation, oldest first.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its messages in the order they were appended.</returns>
    /// <remarks>
    /// The export of §10.2, and an operator operation: it reads across participants by design, which is
    /// exactly why it must never reach an agent's tool set. Unbounded, because an export that silently
    /// truncated would present a partial record as a complete one.
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

    /// <summary>Loads the Request an exchange was opened with.</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="transaction">The transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The Request, or <see langword="null"/> if the exchange carries none any more.</returns>
    public async Task<Message?> GetRequestAsync(
        long exchangeId,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var row = await _connection.QueryFirstOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                $"""
                {SelectColumns}
                WHERE exchange_id = @Exchange AND type = 'request'
                ORDER BY id
                LIMIT 1;
                """,
                new { Exchange = exchangeId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>Loads a single message.</summary>
    /// <param name="id">The message's primary key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The message, or <see langword="null"/>.</returns>
    public async Task<Message?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>Records that a message has been handed to its recipient.</summary>
    /// <param name="id">The message.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if a pending message was marked.</returns>
    /// <remarks>Guarded on still being pending, so a concurrent second drain cannot overwrite the first's stamp.</remarks>
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

    /// <summary>Finds the message an exchange's owing participant would be given again.</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="agent">The participant that owes the next move.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The latest message addressed to it on that exchange, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The latest rather than the whole exchange: giving back everything would hand over messages the
    /// participant already dealt with and ones it sent itself.
    /// </remarks>
    public async Task<Message?> GetLatestOwedAsync(
        long exchangeId,
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                $"""
                {SelectColumns}
                WHERE exchange_id = @Exchange AND to_agent = @Agent
                ORDER BY id DESC
                LIMIT 1;
                """,
                new { Exchange = exchangeId, Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>Returns a delivered message to its recipient's inbox.</summary>
    /// <param name="id">The message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the row exists.</returns>
    /// <remarks>
    /// Re-delivery, not re-sending (§14). A fresh row would make the log a record of delivery attempts
    /// rather than of what was said, and on a fast restart would land inside the deduplication window and
    /// be discarded.
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
