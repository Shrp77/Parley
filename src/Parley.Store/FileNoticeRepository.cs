using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Who fetched which file, and the gone notices waiting for them (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>A notice is raised in the transaction that removes the file</b>, by <see cref="SharedFileRepository"/> or a
/// destroy or purge, and collected in the transaction that drains an inbox. Nothing here wakes anything: a notice
/// waits for whenever the agent next looks.
/// </para>
/// <para>
/// <b>A file's going is noticed, and why</b>: gone, or tampered with, when something other than Parley changed its
/// bytes, since a placed file never changes through Parley. At most one per agent per file, one row per key.
/// </para>
/// <para>
/// A retired agent is not told. A purged one has no rows: its fetch records and notices cascade from it.
/// </para>
/// </remarks>
public sealed class FileNoticeRepository(SqliteConnection connection)
{
    private const string Upsert =
        """
         ON CONFLICT (agent, conversation_id, name_key) DO UPDATE
           SET name = excluded.name, sha256_had = excluded.sha256_had, sha256_now = excluded.sha256_now,
               size_bytes = excluded.size_bytes, changed_utc = excluded.changed_utc;
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Records that an agent wrote and verified one content of a file.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="conversationId">The file's conversation.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="sha256">The hash of the content the agent received.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// A later fetch replaces the record. A notice still waiting about a file of that name is withdrawn: the agent
    /// holds the file as it is there now, which a name removed and placed again may be.
    /// </remarks>
    public async Task RecordFetchAsync(
        AgentName agent,
        string conversationId,
        SharedFileName name,
        ContentHash sha256,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(name);

        using var scope = WriteScope.Begin(_connection, transaction);

        var stamp = ExchangeRow.FormatTimestamp(nowUtc);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO file_fetches (agent, conversation_id, name_key, name, sha256, fetched_utc)
                VALUES (@Agent, @Conversation, @Key, @Name, @Sha256, @When)
                ON CONFLICT (agent, conversation_id, name_key) DO UPDATE
                   SET name = excluded.name, sha256 = excluded.sha256, fetched_utc = excluded.fetched_utc;

                DELETE FROM file_notices WHERE agent = @Agent AND conversation_id = @Conversation AND name_key = @Key;
                """,
                new
                {
                    Agent = agent.Value,
                    Conversation = conversationId,
                    name.Key,
                    Name = name.Value,
                    Sha256 = sha256.Value,
                    When = stamp,
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
    }

    /// <summary>Raises "gone" notices for a file that was removed, pruned, found gone from disk or destroyed.</summary>
    /// <param name="conversationId">The file's conversation.</param>
    /// <param name="nameKey">The file's name key.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="goneUtc">When it went.</param>
    /// <param name="forgetFetches">
    /// Whether to delete the fetch records now. A removal does; a destroy or a purge leaves them for its own
    /// deletes, which report them.
    /// </param>
    /// <param name="transaction">The transaction removing it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task RaiseGoneAsync(
        string conversationId,
        string nameKey,
        string name,
        DateTimeOffset goneUtc,
        bool forgetFetches,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var parameters = new
        {
            Conversation = conversationId,
            Key = nameKey,
            Name = name,
            When = ExchangeRow.FormatTimestamp(goneUtc),
        };

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO file_notices
                    (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
                SELECT ff.agent, ff.conversation_id, ff.name_key, @Name, ff.sha256, NULL, NULL, @When
                  FROM file_fetches ff
                  JOIN agents a ON a.name = ff.agent
                 WHERE ff.conversation_id = @Conversation AND ff.name_key = @Key AND a.status = 'active'
                """ + Upsert,
                parameters,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (forgetFetches)
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM file_fetches WHERE conversation_id = @Conversation AND name_key = @Key;",
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Raises "gone" notices for every file in the location, and forgets every fetch.</summary>
    /// <param name="goneUtc">When they went.</param>
    /// <param name="transaction">The transaction pruning them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task RaiseGoneForAllAsync(
        DateTimeOffset goneUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO file_notices
                    (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
                SELECT ff.agent, ff.conversation_id, ff.name_key, ff.name, ff.sha256, NULL, NULL, @When
                  FROM file_fetches ff
                  JOIN agents a ON a.name = ff.agent
                  JOIN shared_files f ON f.conversation_id = ff.conversation_id AND f.name_key = ff.name_key
                 WHERE a.status = 'active'
                """ + Upsert,
                new { When = ExchangeRow.FormatTimestamp(goneUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM file_fetches
                 WHERE EXISTS (SELECT 1 FROM shared_files f
                                WHERE f.conversation_id = file_fetches.conversation_id
                                  AND f.name_key = file_fetches.name_key);
                """,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Raises "gone" notices for the files of conversations about to be deleted, leaving the fetch records.</summary>
    /// <param name="conversationIds">A SQL expression naming the conversations: an identifier parameter or a table of them.</param>
    /// <param name="parameters">The parameters that expression reads, including <c>@When</c>.</param>
    /// <param name="transaction">The transaction deleting them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the notices are written.</returns>
    /// <remarks>
    /// Internal, and the expression is one of two fixed spellings its callers pass, never anything a caller of the
    /// store supplied. The fetch records stay because the destroy or purge deletes them itself and reports how many
    /// went (SPEC.md §14).
    /// </remarks>
    internal Task RaiseGoneForConversationsAsync(
        ConversationScope conversationIds,
        object parameters,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        _connection.ExecuteAsync(
            new CommandDefinition(
                conversationIds == ConversationScope.One
                    ? """
                      INSERT INTO file_notices
                          (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
                      SELECT ff.agent, ff.conversation_id, ff.name_key, ff.name, ff.sha256, NULL, NULL, @When
                        FROM file_fetches ff
                        JOIN agents a ON a.name = ff.agent
                       WHERE ff.conversation_id = @Conversation AND a.status = 'active'
                      """ + Upsert
                    : """
                      INSERT INTO file_notices
                          (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
                      SELECT ff.agent, ff.conversation_id, ff.name_key, ff.name, ff.sha256, NULL, NULL, @When
                        FROM file_fetches ff
                        JOIN agents a ON a.name = ff.agent
                       WHERE ff.conversation_id IN (SELECT id FROM doomed_conversations)
                         AND ff.agent <> @Name AND a.status = 'active'
                      """ + Upsert,
                parameters,
                transaction,
                cancellationToken: cancellationToken));

    /// <summary>Raises "tampered" notices for a placed file whose bytes on disk no longer hash to what was placed.</summary>
    /// <param name="conversationId">The file's conversation.</param>
    /// <param name="nameKey">The file's name key.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="placed">The hash placed.</param>
    /// <param name="placer">The agent that placed it, or <see langword="null"/> for the person.</param>
    /// <param name="found">The hash of the bytes found.</param>
    /// <param name="foundBytes">Their size.</param>
    /// <param name="whenUtc">When the sweep found it.</param>
    /// <param name="transaction">The transaction removing the file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the notices are raised and the fetch records gone.</returns>
    /// <remarks>
    /// <para>
    /// Every agent that fetched it, and its placer too (SPEC.md §9.5): something other than Parley changed the bytes,
    /// a copy may have spread from them, and the placer is the one that can place the file again. A notice already
    /// waiting about the name takes the latest state, as a gone one does.
    /// </para>
    /// <para>The fetch records go with the file, as a removal's do.</para>
    /// </remarks>
    public async Task RaiseTamperedAsync(
        string conversationId,
        string nameKey,
        string name,
        ContentHash placed,
        AgentName? placer,
        ContentHash found,
        long foundBytes,
        DateTimeOffset whenUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var parameters = new
        {
            Conversation = conversationId,
            Key = nameKey,
            Name = name,
            Placed = placed.Value,
            Placer = placer?.Value,
            Found = found.Value,
            Size = foundBytes,
            When = ExchangeRow.FormatTimestamp(whenUtc),
        };

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO file_notices
                    (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
                SELECT ff.agent, ff.conversation_id, ff.name_key, @Name, ff.sha256, @Found, @Size, @When
                  FROM file_fetches ff
                  JOIN agents a ON a.name = ff.agent
                 WHERE ff.conversation_id = @Conversation AND ff.name_key = @Key AND a.status = 'active'
                   AND ff.sha256 <> @Found
                UNION
                SELECT a.name, @Conversation, @Key, @Name, @Placed, @Found, @Size, @When
                  FROM agents a
                 WHERE a.name = @Placer AND a.status = 'active'
                   AND NOT EXISTS (SELECT 1 FROM file_fetches ff
                                    WHERE ff.agent = a.name AND ff.conversation_id = @Conversation
                                      AND ff.name_key = @Key)
                """ + Upsert + """

                DELETE FROM file_fetches WHERE conversation_id = @Conversation AND name_key = @Key;
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Reads the oldest notices waiting for an agent, deleting nothing.</summary>
    /// <param name="agent">The agent collecting.</param>
    /// <param name="excluding">Keys of notices a collection in flight holds, which no other collection hands over.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// At most <see cref="FileLimits.NoticesPerCollection"/> notices, oldest change first, how many are left besides them
    /// and the held ones, and the keys <see cref="DeleteAsync"/> takes once the agent has them.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Oldest first, so a burst of new removals cannot hold an old notice back. The ones left are not asked for again
    /// the way a limited message drain is: a notice wakes nothing, so they wait for the agent's next look.
    /// </para>
    /// <para>
    /// Read apart from the delete because a collection is recorded only once the agent acknowledges it (SPEC.md §6): a
    /// reply that never arrived would otherwise have taken notices nobody read.
    /// </para>
    /// </remarks>
    public async Task<(FileNoticeBatch Batch, IReadOnlyList<string> Keys)> PeekAsync(
        AgentName agent,
        IReadOnlyCollection<string>? excluding = null,
        CancellationToken cancellationToken = default)
    {
        var held = excluding ?? [];
        var rows = (await _connection.QueryAsync<NoticeRow>(
                new CommandDefinition(
                    $"""
                    SELECT conversation_id AS ConversationId, name_key AS NameKey, name AS Name,
                           sha256_had AS Had, sha256_now AS Found, changed_utc AS ChangedUtc
                      FROM file_notices
                     WHERE agent = @Agent AND {KeyColumn} NOT IN @Held
                     ORDER BY changed_utc, conversation_id, name_key
                     LIMIT @Take;
                    """,
                    new { Agent = agent.Value, Take = FileLimits.NoticesPerCollection, Held = held },
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .AsList();

        var left = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT count(*) FROM file_notices WHERE agent = @Agent AND {KeyColumn} NOT IN @Held;",
                new { Agent = agent.Value, Held = held },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (
            new FileNoticeBatch(
                [.. rows.Select(r => new FileNotice(
                    r.ConversationId,
                    r.Name,
                    ContentHash.Parse(r.Had),
                    ExchangeRow.ParseTimestamp(r.ChangedUtc),
                    r.Found is null ? null : ContentHash.Parse(r.Found)))],
                left - rows.Count),
            [.. rows.Select(r => KeyOf(r.ConversationId, r.NameKey))]);
    }

    /// <summary>Deletes notices an agent has acknowledged collecting.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="keys">The keys <see cref="PeekAsync"/> returned with them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when they are deleted.</returns>
    public async Task DeleteAsync(AgentName agent, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return;
        }

        await _connection.ExecuteAsync(
            new CommandDefinition(
                $"DELETE FROM file_notices WHERE agent = @Agent AND {KeyColumn} IN @Keys;",
                new { Agent = agent.Value, Keys = keys },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // A notice is keyed by its conversation and name key; one string so a set of them can be passed to SQL. Neither part
    // can hold a control character, so the separator cannot occur inside one.
    private const string KeyColumn = "(conversation_id || char(31) || name_key)";

    private static string KeyOf(string conversationId, string nameKey) => conversationId + (char)31 + nameKey;

    /// <summary>How many notices are waiting for an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    public Task<int> CountWaitingAsync(
        AgentName agent,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM file_notices WHERE agent = @Agent;",
                new { Agent = agent.Value },
                transaction,
                cancellationToken: cancellationToken));

    private sealed class NoticeRow
    {
        public string ConversationId { get; init; } = "";

        public string NameKey { get; init; } = "";

        public string Name { get; init; } = "";

        public string Had { get; init; } = "";

        public string? Found { get; init; }

        public string ChangedUtc { get; init; } = "";
    }
}

/// <summary>Which conversations a destroy or a purge is deleting the files of.</summary>
internal enum ConversationScope
{
    /// <summary>The one named by <c>@Conversation</c>.</summary>
    One,

    /// <summary>Those in the purge's <c>doomed_conversations</c> table, except the purged agent's own records.</summary>
    Doomed,
}
