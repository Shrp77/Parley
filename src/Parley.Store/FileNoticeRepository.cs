using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Who fetched which file, and the change notices waiting for them (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>A notice is raised in the transaction that changes the file</b>, by <see cref="SharedFileRepository"/>, and
/// collected in the transaction that drains an inbox. Nothing here wakes anything: a notice waits for whenever
/// the agent next looks, which is the owner's call and the reason a file saved often costs no turns.
/// </para>
/// <para>
/// <b>At most one per agent per file, carrying the latest state</b>, and the schema holds both halves: one row per
/// key, and a row saying nothing changed cannot be stored. So a change back to the content an agent fetched
/// deletes its notice rather than updating it.
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
    /// A later fetch replaces the record, so the next notice compares against it. If the file has changed since the
    /// content the agent received — the report arrived after a replacement — the notice is raised now rather than
    /// waiting for a change that has already happened; otherwise any notice waiting is withdrawn, since the agent
    /// holds the current content.
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

                INSERT INTO file_notices
                    (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
                SELECT @Agent, f.conversation_id, f.name_key, f.name, @Sha256, f.sha256, f.size_bytes, f.changed_utc
                  FROM shared_files f
                 WHERE f.conversation_id = @Conversation AND f.name_key = @Key AND f.sha256 <> @Sha256;
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

    /// <summary>Raises or withdraws notices for a file whose content changed.</summary>
    /// <param name="conversationId">The file's conversation.</param>
    /// <param name="nameKey">The file's name key.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="now">The hash of its content now.</param>
    /// <param name="sizeBytes">Its size now.</param>
    /// <param name="changedUtc">When it changed.</param>
    /// <param name="transaction">The transaction that changed it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task RaiseChangedAsync(
        string conversationId,
        string nameKey,
        string name,
        ContentHash now,
        long sizeBytes,
        DateTimeOffset changedUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default) =>
        _connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM file_notices
                 WHERE conversation_id = @Conversation AND name_key = @Key
                   AND agent IN (SELECT agent FROM file_fetches
                                  WHERE conversation_id = @Conversation AND name_key = @Key AND sha256 = @Now);

                INSERT INTO file_notices
                    (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
                SELECT ff.agent, ff.conversation_id, ff.name_key, @Name, ff.sha256, @Now, @Size, @When
                  FROM file_fetches ff
                  JOIN agents a ON a.name = ff.agent
                 WHERE ff.conversation_id = @Conversation AND ff.name_key = @Key
                   AND ff.sha256 <> @Now AND a.status = 'active'
                """ + Upsert,
                new
                {
                    Conversation = conversationId,
                    Key = nameKey,
                    Name = name,
                    Now = now.Value,
                    Size = sizeBytes,
                    When = ExchangeRow.FormatTimestamp(changedUtc),
                },
                transaction,
                cancellationToken: cancellationToken));

    /// <summary>Raises "gone" notices for a file that was removed, pruned, renamed away or destroyed.</summary>
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

    /// <summary>Hands over the oldest notices waiting for an agent, and deletes them.</summary>
    /// <param name="agent">The agent collecting.</param>
    /// <param name="transaction">The transaction draining its inbox, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>At most <see cref="FileLimits.NoticesPerCollection"/> notices, oldest change first, and how many are left.</returns>
    /// <remarks>
    /// Oldest first, so a burst of new changes cannot hold an old notice back. The ones left are not asked for again
    /// the way a limited message drain is: a notice wakes nothing, so they wait for the agent's next look.
    /// </remarks>
    public async Task<FileNoticeBatch> CollectAsync(
        AgentName agent,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = WriteScope.Begin(_connection, transaction);

        var rows = (await _connection.QueryAsync<NoticeRow>(
                new CommandDefinition(
                    """
                    SELECT conversation_id AS ConversationId, name_key AS NameKey, name AS Name,
                           sha256_had AS Had, sha256_now AS Now, size_bytes AS SizeBytes, changed_utc AS ChangedUtc
                      FROM file_notices
                     WHERE agent = @Agent
                     ORDER BY changed_utc, conversation_id, name_key
                     LIMIT @Take;
                    """,
                    new { Agent = agent.Value, Take = FileLimits.NoticesPerCollection },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .AsList();

        if (rows.Count > 0)
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM file_notices WHERE agent = @Agent AND conversation_id = @ConversationId AND name_key = @NameKey;",
                    rows.Select(r => new { Agent = agent.Value, r.ConversationId, r.NameKey }),
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var left = await CountWaitingAsync(agent, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();

        return new FileNoticeBatch(
            [.. rows.Select(r => new FileNotice(
                r.ConversationId,
                r.Name,
                ContentHash.Parse(r.Had),
                r.Now is null ? null : ContentHash.Parse(r.Now),
                r.SizeBytes,
                ExchangeRow.ParseTimestamp(r.ChangedUtc)))],
            left);
    }

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

        public string? Now { get; init; }

        public long? SizeBytes { get; init; }

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
