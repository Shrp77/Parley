using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>Links between conversations, and moving exchanges from one to another (SPEC.md §5.0).</summary>
/// <remarks>
/// <para>
/// <b>The one place an exchange changes conversation.</b> Nothing else writes <c>exchanges.conversation_id</c> after an
/// exchange opens. Only the operator side reaches this, because an agent that could split a line of work is what §5.0
/// exists to prevent.
/// </para>
/// <para>
/// <b>Every refusal says why, and storage holds the ones that matter.</b> The checks here exist to give a sentence;
/// <c>tr_a_moved_exchange_may_go_there</c> is what holds if one of them is ever wrong.
/// </para>
/// </remarks>
/// <param name="connection">The store.</param>
public sealed partial class ConversationMoveRepository(SqliteConnection connection)
{
    private const int FirstWordsLength = 120;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Opens a new conversation with a parent, sending nothing.</summary>
    /// <param name="parent">The conversation it comes from.</param>
    /// <param name="child">The new conversation's name.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Why it was refused, or <see langword="null"/> when the link is recorded.</returns>
    /// <remarks>
    /// Recorded before the Request that follows it, so a Request can never reach a spin-off with no link behind it. A surface
    /// whose send reaches nobody takes it back with <see cref="AbandonSpinOffAsync"/>. Asking again with the same two names
    /// succeeds, for a surface that has not.
    /// </remarks>
    public async Task<string?> SpinOffAsync(
        string parent,
        string child,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(parent);
        ArgumentException.ThrowIfNullOrEmpty(child);

        using var scope = WriteScope.Begin(_connection, null);

        if (string.Equals(parent, child, StringComparison.Ordinal))
        {
            return "A conversation cannot be its own parent.";
        }

        if (!await ExistsAsync(parent, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            return $"There is no conversation called {parent} to start from.";
        }

        if (await NewNameProblemAsync(child, scope.Transaction, cancellationToken).ConfigureAwait(false) is { } unusable)
        {
            return unusable;
        }

        if (await ExistsAsync(child, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            var existing = await _connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    "SELECT parent FROM conversation_links WHERE child = @Child;",
                    new { Child = child },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if ((existing is not null && !string.Equals(existing, parent, StringComparison.Ordinal))
                || await HoldsAnythingAsync(child, scope.Transaction, cancellationToken).ConfigureAwait(false))
            {
                return $"There is already a conversation called {child}.";
            }

            if (existing is not null)
            {
                return null;
            }
        }

        await new ConversationRepository(_connection)
            .EnsureOpenAsync(child, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

        await LinkAsync(child, parent, "spinoff", nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();

        return null;
    }

    /// <summary>Takes back a link whose first Request reached nobody, leaving no trace of the attempt.</summary>
    /// <param name="parent">The conversation it came from.</param>
    /// <param name="child">The conversation that was to be started.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether anything was taken back.</returns>
    /// <remarks>
    /// So a record never says a conversation was spun off when nothing was ever said in it, and the name stays free. A child
    /// holding anything is left alone: the link is true of it by then.
    /// </remarks>
    public async Task<bool> AbandonSpinOffAsync(string parent, string child, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(parent);
        ArgumentException.ThrowIfNullOrEmpty(child);

        using var scope = WriteScope.Begin(_connection, null);

        var linked = await _connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM conversation_links WHERE child = @Child AND parent = @Parent AND how = 'spinoff');",
                new { Child = child, Parent = parent },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (!linked || await HoldsAnythingAsync(child, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM conversation_links WHERE child = @Child;
                DELETE FROM conversations WHERE id = @Child;
                """,
                new { Child = child },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();

        return true;
    }

    /// <summary>Says what moving some exchanges would take, changing nothing.</summary>
    /// <param name="source">The conversation they are in.</param>
    /// <param name="exchanges">The exchanges picked.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every exchange that would go, and the files that stay.</returns>
    public async Task<MovePreview> PreviewAsync(
        string source,
        IReadOnlyCollection<long> exchanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(exchanges);

        if (await SourceProblemAsync(source, exchanges, null, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return new MovePreview([], [], problem);
        }

        var picked = await ReadExchangesAsync(exchanges, null, cancellationToken).ConfigureAwait(false);

        var absent = exchanges.Where(id => picked.All(row => row.Id != id || row.ConversationId != source)).ToList();

        if (absent.Count > 0)
        {
            return new MovePreview([], [], NotInSource(absent, source));
        }

        var withGroups = await ExpandGroupsAsync(source, picked, null, cancellationToken).ConfigureAwait(false);
        var previews = new List<MovePreviewExchange>();

        foreach (var row in withGroups.OrderBy(r => r.Id))
        {
            var held = (await _connection.QueryAsync<HeldMessage>(
                new CommandDefinition(
                    "SELECT id AS Id, type AS Type, payload_json AS PayloadJson FROM messages WHERE exchange_id = @Id ORDER BY id;",
                    new { row.Id },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            var request = held.FirstOrDefault(m => string.Equals(m.Type, "request", StringComparison.Ordinal));

            previews.Add(new MovePreviewExchange(
                row.Id,
                !string.Equals(row.State, "CLOSED", StringComparison.Ordinal),
                AgentName.Create(row.Requester),
                AgentName.Create(row.Receiver),
                held.Count,
                request is null ? string.Empty : FirstWords(PayloadShape.Spoken(request.PayloadJson)),
                exchanges.Contains(row.Id) ? null : "It was asked of a role with the others, which go together."));
        }

        var files = await _connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT name FROM shared_files WHERE conversation_id = @Source ORDER BY name;",
                new { Source = source },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new MovePreview(previews, [.. files], null);
    }

    /// <summary>Moves whole exchanges to another conversation, all or none.</summary>
    /// <param name="source">The conversation they are in.</param>
    /// <param name="destination">The conversation they go to.</param>
    /// <param name="isNew">Whether the destination must not exist yet, and takes the source as its parent.</param>
    /// <param name="exchanges">The exchanges, with every member of any group among them.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many moved, or why none did.</returns>
    /// <remarks>
    /// <para>
    /// <b>One transaction, with the composite keys deferred</b> as destroy does it, because an exchange and its messages
    /// have to change together and neither can go first. A miss fails at commit and nothing has happened.
    /// </para>
    /// <para>
    /// <b>The list must be whole.</b> <see cref="PreviewAsync"/> adds what a group needs and says so, and this refuses a
    /// list that leaves part of one behind rather than adding to it, so nothing moves that the person was not shown.
    /// </para>
    /// <para>
    /// Wakes nobody, and has no way to. An agent party to an open exchange is told at its next collection.
    /// </para>
    /// </remarks>
    public async Task<MoveOutcome> MoveAsync(
        string source,
        string destination,
        bool isNew,
        IReadOnlyCollection<long> exchanges,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(exchanges);

        using var scope = WriteScope.Begin(_connection, null);
        var transaction = scope.Transaction;

        static MoveOutcome Refuse(string why) => new(why, 0, 0);

        if (await SourceProblemAsync(source, exchanges, transaction, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return Refuse(problem);
        }

        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return Refuse("Those entries are already in that conversation.");
        }

        if (isNew)
        {
            if (await NewNameProblemAsync(destination, transaction, cancellationToken).ConfigureAwait(false) is { } unusable)
            {
                return Refuse(unusable);
            }

            if (await ExistsAsync(destination, transaction, cancellationToken).ConfigureAwait(false))
            {
                return Refuse($"There is already a conversation called {destination}.");
            }
        }
        else if (await ExistingDestinationProblemAsync(destination, transaction, cancellationToken).ConfigureAwait(false) is { } closed)
        {
            return Refuse(closed);
        }

        var rows = await ReadExchangesAsync(exchanges, transaction, cancellationToken).ConfigureAwait(false);
        var absent = exchanges.Where(id => rows.All(row => row.Id != id || row.ConversationId != source)).ToList();

        if (absent.Count > 0)
        {
            return Refuse(NotInSource(absent, source));
        }

        var whole = await ExpandGroupsAsync(source, rows, transaction, cancellationToken).ConfigureAwait(false);

        if (whole.Count != rows.Count)
        {
            return Refuse("Part of a request to a role would be left behind. Those exchanges go together, so look at the list again.");
        }

        var ids = rows.Select(r => r.Id).OrderBy(id => id).ToList();

        if (isNew)
        {
            await new ConversationRepository(_connection)
                .EnsureOpenAsync(destination, nowUtc, transaction, cancellationToken).ConfigureAwait(false);

            await LinkAsync(destination, source, "move", nowUtc, transaction, cancellationToken).ConfigureAwait(false);
        }

        var messageIds = (await _connection.QueryAsync<long>(
            new CommandDefinition(
                "SELECT id FROM messages WHERE exchange_id IN @Ids ORDER BY id;",
                new { Ids = ids },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        await RunAsync("PRAGMA defer_foreign_keys = ON;", null).ConfigureAwait(false);

        var parameters = new { Ids = ids, Destination = destination };

        await RunAsync("UPDATE exchanges SET conversation_id = @Destination WHERE id IN @Ids;", parameters).ConfigureAwait(false);
        await RunAsync("UPDATE messages SET conversation_id = @Destination WHERE exchange_id IN @Ids;", parameters).ConfigureAwait(false);
        await RunAsync("UPDATE conversation_cycles SET conversation_id = @Destination WHERE exchange_id IN @Ids;", parameters).ConfigureAwait(false);

        // A length record follows its message, and its position is that message's place in the destination by the broker's
        // order. Records of pruned messages have no exchange to trace them by, so they stay where they are, and no record is lost.
        await RunAsync(
            "UPDATE message_lengths SET conversation_id = @Destination WHERE message_id IN (SELECT id FROM messages WHERE exchange_id IN @Ids);",
            parameters).ConfigureAwait(false);

        // Both conversations, because position means how far into its conversation a message is.
        await RunAsync(
            """
            UPDATE message_lengths
               SET position = (SELECT count(*) FROM message_lengths other
                                WHERE other.conversation_id = message_lengths.conversation_id
                                  AND other.message_id <= message_lengths.message_id)
             WHERE conversation_id IN (@Source, @Destination);
            """,
            new { Source = source, Destination = destination }).ConfigureAwait(false);

        var conversations = new ConversationRepository(_connection);

        foreach (var row in rows)
        {
            // Voids a poll in the destination, as any exchange joining it does.
            await conversations.JoinAsync(
                destination, AgentName.Create(row.Requester), AgentName.Create(row.Receiver), nowUtc, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        // Who would be asked has changed, here as much as there (§5.0). A no-op on a closed conversation.
        _ = await conversations.VoidPollAsync(source, transaction, cancellationToken).ConfigureAwait(false);

        await RunAsync(
            """
            DELETE FROM conversation_participants
             WHERE conversation_id = @Source
               AND agent IN @Parties
               AND NOT EXISTS (SELECT 1 FROM exchanges e
                                WHERE e.conversation_id = @Source
                                  AND (e.requester = conversation_participants.agent
                                       OR e.receiver = conversation_participants.agent))
               AND NOT EXISTS (SELECT 1 FROM shared_files f
                                WHERE f.conversation_id = @Source
                                  AND f.placed_by = conversation_participants.agent);
            """,
            new { Source = source, Parties = rows.SelectMany(r => new[] { r.Requester, r.Receiver }).Distinct().ToList() })
            .ConfigureAwait(false);

        var stamp = ExchangeRow.FormatTimestamp(nowUtc);

        var person = await _connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT name FROM agents WHERE adapter = 'parley-console' LIMIT 1;",
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var move = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO conversation_moves (source, destination, moved_by, moved_utc, first_message, messages)
                VALUES (@Source, @Destination, @MovedBy, @When, @First, @Messages)
                RETURNING id;
                """,
                new
                {
                    Source = source,
                    Destination = destination,
                    MovedBy = person ?? "the person",
                    When = stamp,
                    First = messageIds.Count == 0 ? (long?)null : messageIds[0],
                    Messages = messageIds.Count
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var id in ids)
        {
            await RunAsync(
                "INSERT INTO conversation_move_exchanges (move_id, exchange_id) VALUES (@Move, @Id);",
                new { Move = move, Id = id }).ConfigureAwait(false);
        }

        // An agent party to work still open is told. A person reads the conversation, the facilitator takes no part in
        // a move, and a retired agent is told nothing, as a gone notice does not tell one.
        await RunAsync(
            $"""
            INSERT INTO move_notices (agent, exchange_id, from_conversation, to_conversation, created_utc)
            SELECT a.name, e.id, @Source, @Destination, @When
              FROM exchanges e
              JOIN agents a ON a.name IN (e.requester, e.receiver)
             WHERE e.id IN @Ids
               AND e.state <> 'CLOSED'
               AND a.status = 'active'
               AND a.adapter NOT IN ('parley-console', '{Facilitation.Adapter}')
             ORDER BY e.id, a.name;
            """,
            new { Source = source, Destination = destination, When = stamp, Ids = ids }).ConfigureAwait(false);

        scope.Complete();

        return new MoveOutcome(null, ids.Count, messageIds.Count);

        Task RunAsync(string sql, object? arguments) => _connection.ExecuteAsync(
            new CommandDefinition(sql, arguments, transaction, cancellationToken: cancellationToken));
    }

    /// <summary>Reads a conversation's parent and children.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its links.</returns>
    public async Task<ConversationLinks> GetLinksAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var parent = await _connection.QuerySingleOrDefaultAsync<LinkRow>(
            new CommandDefinition(
                "SELECT parent AS Parent, how AS How FROM conversation_links WHERE child = @Id;",
                new { Id = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var children = await _connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT child FROM conversation_links WHERE parent = @Id ORDER BY linked_utc, child;",
                new { Id = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new ConversationLinks(
            parent?.Parent,
            parent is null ? null : string.Equals(parent.How, "move", StringComparison.Ordinal) ? ConversationLinkKind.Move : ConversationLinkKind.SpinOff,
            [.. children]);
    }

    /// <summary>Reads every move in or out of a conversation, oldest first.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The moves.</returns>
    public async Task<IReadOnlyList<MoveRecord>> GetMovesAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var rows = (await _connection.QueryAsync<MoveRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, source AS Source, destination AS Destination, moved_by AS MovedBy, moved_utc AS MovedUtc,
                       first_message AS FirstMessage, messages AS Messages
                  FROM conversation_moves
                 WHERE source = @Id OR destination = @Id
                 ORDER BY id;
                """,
                new { Id = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var records = new List<MoveRecord>(rows.Count);

        foreach (var row in rows)
        {
            var exchanges = await _connection.QueryAsync<long>(
                new CommandDefinition(
                    "SELECT exchange_id FROM conversation_move_exchanges WHERE move_id = @Id ORDER BY exchange_id;",
                    new { row.Id },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            records.Add(new MoveRecord(
                row.Id,
                row.Source,
                row.Destination,
                row.MovedBy,
                ExchangeRow.ParseTimestamp(row.MovedUtc),
                row.FirstMessage,
                (int)row.Messages,
                [.. exchanges]));
        }

        return records;
    }

    /// <summary>Reads settled pairs one of whose exchanges is in this conversation and the other is not.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Each such pair, naming the other conversation.</returns>
    public async Task<IReadOnlyList<SettledElsewhere>> GetSettledElsewhereAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<SettledRow>(
            new CommandDefinition(
                """
                SELECT e.id AS Exchange, e.settled_by AS SettledBy,
                       CASE WHEN e.conversation_id = @Id THEN o.conversation_id ELSE e.conversation_id END AS Conversation
                  FROM exchanges e
                  JOIN exchanges o ON o.id = e.settled_by
                 WHERE (e.conversation_id = @Id) <> (o.conversation_id = @Id)
                   AND (e.conversation_id = @Id OR o.conversation_id = @Id)
                 ORDER BY e.id;
                """,
                new { Id = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new SettledElsewhere(r.Exchange, r.SettledBy, r.Conversation))];
    }

    /// <summary>Reads the move notices waiting for an agent, oldest first, marking nothing.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="limit">The most to read.</param>
    /// <param name="excluding">Notices a collection in flight already holds.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The notices, and their identifiers for acknowledging them.</returns>
    public async Task<(IReadOnlyList<MoveNotice> Notices, IReadOnlyList<long> Ids)> PeekNoticesAsync(
        AgentName agent,
        int limit,
        IReadOnlyCollection<long>? excluding = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = (await _connection.QueryAsync<NoticeRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, exchange_id AS Exchange, from_conversation AS FromConversation, to_conversation AS ToConversation
                  FROM move_notices
                 WHERE agent = @Agent AND delivered_utc IS NULL AND id NOT IN @Held
                 ORDER BY id
                 LIMIT @Limit;
                """,
                new { Agent = agent.Value, Limit = limit, Held = excluding ?? [] },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        return (
            [.. rows.Select(r => new MoveNotice(r.Exchange, r.FromConversation, r.ToConversation))],
            [.. rows.Select(r => r.Id)]);
    }

    /// <summary>Counts the move notices waiting for an agent beyond those named.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="excluding">Notices handed over or held.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many.</returns>
    public Task<int> CountNoticesAsync(
        AgentName agent,
        IReadOnlyCollection<long> excluding,
        CancellationToken cancellationToken = default) =>
        _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM move_notices WHERE agent = @Agent AND delivered_utc IS NULL AND id NOT IN @Held;",
                new { Agent = agent.Value, Held = excluding },
                cancellationToken: cancellationToken));

    /// <summary>Records notices an agent has acknowledged collecting.</summary>
    /// <param name="ids">The identifiers <see cref="PeekNoticesAsync"/> returned.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when they are marked.</returns>
    public Task MarkNoticesDeliveredAsync(
        IReadOnlyCollection<long> ids,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ids.Count == 0
            ? Task.CompletedTask
            : _connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE move_notices SET delivered_utc = @When WHERE id IN @Ids AND delivered_utc IS NULL;",
                    new { Ids = ids, When = ExchangeRow.FormatTimestamp(nowUtc) },
                    cancellationToken: cancellationToken));

    private static string NotInSource(IReadOnlyList<long> absent, string source) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Exchange {string.Join(", ", absent)} is not in {source}. It may have moved already, so look at the conversation again.");

    private static string FirstWords(string said)
    {
        var flat = string.Join(' ', said.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return flat.Length <= FirstWordsLength
            ? flat
            : string.Concat(flat.AsSpan(0, char.IsHighSurrogate(flat[FirstWordsLength - 2]) ? FirstWordsLength - 2 : FirstWordsLength - 1), "…");
    }

    private Task LinkAsync(
        string child, string parent, string how, DateTimeOffset nowUtc, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        _connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO conversation_links (child, parent, how, linked_utc) VALUES (@Child, @Parent, @How, @When);",
                new { Child = child, Parent = parent, How = how, When = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken));

    private Task<bool> ExistsAsync(string conversation, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        _connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM conversations WHERE id = @Id);",
                new { Id = conversation },
                transaction,
                cancellationToken: cancellationToken));

    private Task<bool> HoldsAnythingAsync(string conversation, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        _connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM exchanges WHERE conversation_id = @Id)
                    OR EXISTS (SELECT 1 FROM shared_files WHERE conversation_id = @Id)
                    OR EXISTS (SELECT 1 FROM conversation_participants WHERE conversation_id = @Id)
                    OR EXISTS (SELECT 1 FROM conversation_moves WHERE source = @Id OR destination = @Id)
                    OR EXISTS (SELECT 1 FROM conversation_links WHERE parent = @Id);
                """,
                new { Id = conversation },
                transaction,
                cancellationToken: cancellationToken));

    /// <summary>Why a name cannot be given to a new conversation, whether or not one exists under it.</summary>
    private async Task<string?> NewNameProblemAsync(string name, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        if (!ValidName().IsMatch(name))
        {
            return "A conversation's name is letters, digits, '.', '_' and '-', up to 64 characters.";
        }

        if (IsOwnership(name))
        {
            return "A name beginning owns- belongs to the facilitator's conversations.";
        }

        var (destroyed, reserved) = await _connection.QuerySingleAsync<(bool, bool)>(
            new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM destroyed_conversations WHERE id = @Name),
                       EXISTS (SELECT 1 FROM roadmap_item_conversations WHERE conversation_id = @Name);
                """,
                new { Name = name },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return destroyed
            ? $"A conversation called {name} was destroyed, and that name stays taken."
            : reserved
                ? $"The name {name} is reserved for a roadmap item."
                : null;
    }

    private async Task<string?> SourceProblemAsync(
        string source, IReadOnlyCollection<long> exchanges, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        if (exchanges.Count == 0)
        {
            return "Nothing was picked, so nothing would move.";
        }

        if (!await ExistsAsync(source, transaction, cancellationToken).ConfigureAwait(false))
        {
            return $"There is no conversation called {source}.";
        }

        return await KindProblemAsync(source, transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExistingDestinationProblemAsync(
        string destination, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var state = await _connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT state FROM conversations WHERE id = @Id;",
                new { Id = destination },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return state switch
        {
            null => $"There is no conversation called {destination}.",
            "CLOSED" => $"{destination} is closed, and a closed conversation stays closed.",
            _ => await KindProblemAsync(destination, transaction, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<string?> KindProblemAsync(string conversation, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        if (IsOwnership(conversation))
        {
            return $"{conversation} is an ownership conversation, and those take part in no move.";
        }

        var reserved = await _connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM roadmap_item_conversations WHERE conversation_id = @Id);",
                new { Id = conversation },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return reserved ? $"{conversation} is a roadmap item's conversation, and those take part in no move." : null;
    }

    private static bool IsOwnership(string conversation) =>
        conversation.StartsWith(Facilitation.ConversationPrefix, StringComparison.Ordinal);

    private async Task<List<ExchangeFacts>> ReadExchangesAsync(
        IReadOnlyCollection<long> exchanges, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        (await _connection.QueryAsync<ExchangeFacts>(
            new CommandDefinition(
                """
                SELECT id AS Id, conversation_id AS ConversationId, state AS State, requester AS Requester,
                       receiver AS Receiver, request_group AS RequestGroup
                  FROM exchanges
                 WHERE id IN @Ids;
                """,
                new { Ids = exchanges },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

    /// <summary>What was given, with every other member of any group among it.</summary>
    private async Task<List<ExchangeFacts>> ExpandGroupsAsync(
        string source, List<ExchangeFacts> rows, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var groups = rows.Where(r => r.RequestGroup is not null).Select(r => r.RequestGroup!).Distinct(StringComparer.Ordinal).ToList();

        if (groups.Count == 0)
        {
            return rows;
        }

        var members = await _connection.QueryAsync<ExchangeFacts>(
            new CommandDefinition(
                """
                SELECT id AS Id, conversation_id AS ConversationId, state AS State, requester AS Requester,
                       receiver AS Receiver, request_group AS RequestGroup
                  FROM exchanges
                 WHERE request_group IN @Groups AND conversation_id = @Source;
                """,
                new { Groups = groups, Source = source },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Concat(members).DistinctBy(r => r.Id)];
    }

    [GeneratedRegex(@"\A(?!.*\.\.)[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidName();

    private sealed class ExchangeFacts
    {
        public long Id { get; init; }

        public string ConversationId { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;

        public string Requester { get; init; } = string.Empty;

        public string Receiver { get; init; } = string.Empty;

        public string? RequestGroup { get; init; }
    }

    private sealed class HeldMessage
    {
        public long Id { get; init; }

        public string Type { get; init; } = string.Empty;

        public string PayloadJson { get; init; } = string.Empty;
    }

    private sealed class LinkRow
    {
        public string Parent { get; init; } = string.Empty;

        public string How { get; init; } = string.Empty;
    }

    private sealed class MoveRow
    {
        public long Id { get; init; }

        public string Source { get; init; } = string.Empty;

        public string Destination { get; init; } = string.Empty;

        public string MovedBy { get; init; } = string.Empty;

        public string MovedUtc { get; init; } = string.Empty;

        public long? FirstMessage { get; init; }

        public long Messages { get; init; }
    }

    private sealed class SettledRow
    {
        public long Exchange { get; init; }

        public long SettledBy { get; init; }

        public string Conversation { get; init; } = string.Empty;
    }

    private sealed class NoticeRow
    {
        public long Id { get; init; }

        public long Exchange { get; init; }

        public string FromConversation { get; init; } = string.Empty;

        public string ToConversation { get; init; } = string.Empty;
    }
}
