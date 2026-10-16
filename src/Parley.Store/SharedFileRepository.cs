using System.Buffers.Text;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A shared file as the store holds it: what every surface sees, and the modification time the reconcile compares.
/// </summary>
/// <param name="File">The file.</param>
/// <param name="NameKey">The key its name is compared by.</param>
/// <param name="ModifiedTicks">The file's last-write time when it was last reconciled.</param>
public sealed record StoredFile(SharedFile File, string NameKey, long ModifiedTicks);

/// <summary>
/// A file named by a caller, found or refused.
/// </summary>
/// <param name="File">The file, when exactly one matched.</param>
/// <param name="Refusal">Why nothing matched, or why the match could not be used.</param>
public sealed record FileResolution(StoredFile? File, FileRefusal? Refusal);

/// <summary>
/// What a placement came to in the store.
/// </summary>
/// <param name="Outcome">Placed, replaced, already current, or refused.</param>
/// <param name="File">The file as it now stands, or as it stood when the placement was refused.</param>
/// <param name="Previous">The hash replaced, when the outcome is <see cref="PlaceOutcome.Replaced"/>.</param>
/// <param name="CharactersLeft">How long a name the folder allows, when the outcome is <see cref="PlaceOutcome.DoesNotFit"/>.</param>
public sealed record PlaceResult(PlaceOutcome Outcome, SharedFile? File, ContentHash? Previous, int CharactersLeft);

/// <summary>
/// The files in the file location: their folders, who placed them, and every change to them (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows, never bytes.</b> What is on disk is the server's; this is what is known about it. A placement's rows are
/// written in the same transaction the server moves the bytes into place under, so a file is listed only once
/// both have happened.
/// </para>
/// <para>
/// <b>Every change raises its notices in the same transaction</b>, through <see cref="FileNoticeRepository"/>. A
/// change committed without its notices would be a change nobody who fetched the file is ever told about, and
/// there is no later moment that could notice the omission.
/// </para>
/// <para>
/// <b>Who placed a file is read from the row inside the transaction that acts on it</b>, never from anything the
/// caller says. The schema refuses rewriting it, so a replacement, an edit and a rename all update in
/// place.
/// </para>
/// </remarks>
public sealed class SharedFileRepository(SqliteConnection connection)
{
    private const string Select =
        """
        SELECT f.conversation_id AS ConversationId, c.folder AS Folder, f.name AS Name, f.name_key AS NameKey,
               f.size_bytes AS SizeBytes, f.sha256 AS Sha256, f.placed_by AS PlacedBy,
               f.placed_utc AS PlacedUtc, f.changed_utc AS ChangedUtc, f.modified_ticks AS ModifiedTicks
          FROM shared_files f
          JOIN conversation_folders c ON c.conversation_id = f.conversation_id
        """;

    // The unit separator between a page token's conversation and name key. Neither can contain it.
    private const char TokenSeparator = (char)0x1F;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly FileNoticeRepository _notices = new(connection);

    /// <summary>The folder a conversation's files live in, if it has one yet.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The folder name, or <see langword="null"/>.</returns>
    public Task<string?> GetFolderAsync(
        string conversationId,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return _connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT folder FROM conversation_folders WHERE conversation_id = @Conversation;",
                new { Conversation = conversationId },
                transaction,
                cancellationToken: cancellationToken));
    }

    /// <summary>The conversation a folder belongs to, if any does.</summary>
    /// <param name="folder">The folder name, compared without regard to case as the disks holding it compare it.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The conversation, or <see langword="null"/> when no row names the folder.</returns>
    public Task<string?> GetConversationForFolderAsync(
        string folder,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        return _connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT conversation_id FROM conversation_folders WHERE folder = @Folder;",
                new { Folder = folder },
                transaction,
                cancellationToken: cancellationToken));
    }

    /// <summary>Every conversation's recorded folder, including those holding no file now.</summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Each conversation and its folder name.</returns>
    /// <remarks>
    /// What a scan of the whole location walks: an empty folder can still be given a file by hand, and only a recorded
    /// folder traces that file to a conversation.
    /// </remarks>
    public async Task<IReadOnlyList<(string ConversationId, string Folder)>> GetFoldersAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<(string ConversationId, string Folder)>(
            new CommandDefinition(
                "SELECT conversation_id, folder FROM conversation_folders ORDER BY conversation_id;",
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>The folders of every conversation a purge of an agent would delete.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The folder names, read before the purge so the server can delete them after it.</returns>
    /// <remarks>
    /// The same three routes into a conversation <see cref="AgentRepository.PurgeAsync(AgentName, DateTimeOffset, SqliteTransaction?, CancellationToken)"/>
    /// follows — a participant row, an exchange, a message — so the folders deleted are exactly the conversations
    /// deleted. A purge that followed a narrower rule would leave folders on disk no row names.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetFoldersPurgingWouldDeleteAsync(
        AgentName agent,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var folders = await _connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT folder FROM conversation_folders
                 WHERE conversation_id IN (
                     SELECT conversation_id FROM conversation_participants WHERE agent = @Name
                     UNION
                     SELECT conversation_id FROM exchanges
                      WHERE requester = @Name OR receiver = @Name OR owed_by = @Name
                     UNION
                     SELECT conversation_id FROM messages WHERE from_agent = @Name OR to_agent = @Name);
                """,
                new { Name = agent.Value },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return folders.AsList();
    }

    /// <summary>Gives a conversation its folder, choosing the name the first time.</summary>
    /// <param name="conversationId">The conversation, which must exist.</param>
    /// <param name="takenOnDisk">Whether a folder name is already taken by a directory on disk.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The folder name.</returns>
    /// <remarks>
    /// Chosen once and never again (SPEC.md §9.5): a folder recorded here keeps its name while it is empty, so a
    /// file a person copies into it later is still traced to its conversation. A directory already on disk counts
    /// as taken even when no row names it, so a new conversation never adopts a stray folder's files.
    /// </remarks>
    public async Task<string> EnsureFolderAsync(
        string conversationId,
        Func<string, bool> takenOnDisk,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(takenOnDisk);

        using var scope = WriteScope.Begin(_connection, transaction);

        var existing = await GetFolderAsync(conversationId, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            scope.Complete();
            return existing;
        }

        var recorded = (await _connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT folder FROM conversation_folders;",
                    transaction: scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var folder = ConversationFolder.Choose(conversationId, f => recorded.Contains(f) || takenOnDisk(f));

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO conversation_folders (conversation_id, folder, created_utc)
                VALUES (@Conversation, @Folder, @When);
                """,
                new { Conversation = conversationId, Folder = folder, When = ExchangeRow.FormatTimestamp(nowUtc) },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
        return folder;
    }

    /// <summary>Loads one file.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="name">The name, compared by its key.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file, or <see langword="null"/>.</returns>
    public async Task<StoredFile?> GetAsync(
        string conversationId,
        SharedFileName name,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(name);

        var row = await _connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(
                Select + "\n WHERE f.conversation_id = @Conversation AND f.name_key = @Key;",
                new { Conversation = conversationId, name.Key },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>Every file in the location, for a reconcile of the whole of it.</summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The files.</returns>
    public async Task<IReadOnlyList<StoredFile>> GetAllAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<Row>(
            new CommandDefinition(
                Select + "\n ORDER BY f.conversation_id, f.name_key;",
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>Finds the file a caller named, by conversation and name or by name alone.</summary>
    /// <param name="reference">What the caller named.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file, or why there is not exactly one.</returns>
    /// <remarks>
    /// A name alone is looked for across the whole location: one match is used, and
    /// several is a refusal that does nothing and names the conversations holding it — at most
    /// <see cref="FileLimits.ConversationsPerRefusal"/>, with a count of the rest.
    /// </remarks>
    public async Task<FileResolution> ResolveAsync(
        FileRef reference,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var problem = SharedFileName.Check(reference.Name);

        if (problem != SharedFileNameProblem.None)
        {
            return new FileResolution(
                null,
                FileRefusal.Of(
                    reference,
                    problem == SharedFileNameProblem.ExtensionNotAllowed
                        ? FileRejection.ExtensionNotAllowed
                        : FileRejection.BadName));
        }

        var name = SharedFileName.Create(reference.Name);

        if (!string.IsNullOrEmpty(reference.ConversationId))
        {
            var file = await GetAsync(reference.ConversationId, name, transaction, cancellationToken).ConfigureAwait(false);

            return file is null
                ? new FileResolution(null, FileRefusal.Of(reference, FileRejection.NoSuchFile))
                : new FileResolution(file, null);
        }

        var matches = (await _connection.QueryAsync<Row>(
                new CommandDefinition(
                    Select + "\n WHERE f.name_key = @Key ORDER BY f.conversation_id LIMIT @Take;",
                    new { name.Key, Take = FileLimits.ConversationsPerRefusal + 1 },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .AsList();

        if (matches.Count == 0)
        {
            return new FileResolution(null, FileRefusal.Of(reference, FileRejection.NoSuchFile));
        }

        if (matches.Count == 1)
        {
            return new FileResolution(matches[0].ToDomain(), null);
        }

        var holding = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM shared_files WHERE name_key = @Key;",
                new { name.Key },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var listed = matches.Take(FileLimits.ConversationsPerRefusal).Select(m => m.ConversationId).ToList();

        return new FileResolution(
            null,
            new FileRefusal(reference, FileRejection.AmbiguousName, null, listed, holding - listed.Count));
    }

    /// <summary>Records a placement: a new file, a replacement by its placer, or nothing.</summary>
    /// <param name="conversationId">The conversation, which must exist.</param>
    /// <param name="name">The name.</param>
    /// <param name="sha256">The broker's hash of the bytes received.</param>
    /// <param name="sizeBytes">How many bytes were received.</param>
    /// <param name="placedBy">The attached agent placing it.</param>
    /// <param name="modifiedTicks">The last-write time of the file as it will stand on disk.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="takenOnDisk">Whether a folder name is taken by a directory on disk, for a conversation's first file.</param>
    /// <param name="transaction">The transaction the server moves the bytes under, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What the placement came to.</returns>
    /// <remarks>
    /// <para>
    /// <b>Concurrent placements of one new name have exactly one winner</b>, and the schema decides it: the loser's
    /// insert fails on the primary key and is answered as placed by the winner, never as an error.
    /// </para>
    /// <para>
    /// Identical content is no change at all: nothing is written, counted or raised, and the placement time is
    /// unchanged.
    /// </para>
    /// </remarks>
    public async Task<PlaceResult> PlaceAsync(
        string conversationId,
        SharedFileName name,
        ContentHash sha256,
        long sizeBytes,
        AgentName placedBy,
        long modifiedTicks,
        DateTimeOffset nowUtc,
        Func<string, bool> takenOnDisk,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(takenOnDisk);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

        using var scope = WriteScope.Begin(_connection, transaction);

        var existing = await GetAsync(conversationId, name, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            var result = await ReplaceAsync(
                existing, sha256, sizeBytes, placedBy, modifiedTicks, nowUtc, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);

            scope.Complete();
            return result;
        }

        var folder = await EnsureFolderAsync(conversationId, takenOnDisk, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (!name.FitsIn(folder))
        {
            scope.Complete();
            return new PlaceResult(PlaceOutcome.DoesNotFit, null, null, SharedFileName.CharactersLeftIn(folder));
        }

        var stamp = ExchangeRow.FormatTimestamp(nowUtc);

        try
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO shared_files
                        (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc,
                         modified_ticks)
                    VALUES (@Conversation, @Key, @Name, @Size, @Sha256, @PlacedBy, @When, @When, @Ticks);
                    """,
                    new
                    {
                        Conversation = conversationId,
                        name.Key,
                        Name = name.Value,
                        Size = sizeBytes,
                        Sha256 = sha256.Value,
                        PlacedBy = placedBy.Value,
                        When = stamp,
                        Ticks = modifiedTicks,
                    },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException conflict) when (conflict.SqliteErrorCode == 19)
        {
            // The concurrent new name. Answered from the winner's row, which is what the loser needs to know.
            var winner = await GetAsync(conversationId, name, scope.Transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A placement conflicted with a row that is not there.", conflict);

            var lost = await ReplaceAsync(
                winner, sha256, sizeBytes, placedBy, modifiedTicks, nowUtc, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);

            scope.Complete();
            return lost;
        }

        await CountPlacementAsync(placedBy, sizeBytes, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

        var placed = await GetAsync(conversationId, name, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return new PlaceResult(PlaceOutcome.Placed, placed!.File, null, 0);
    }

    /// <summary>Records a file that appeared in a folder without being placed, as the person's.</summary>
    /// <param name="conversationId">The conversation whose folder it is in.</param>
    /// <param name="name">The name.</param>
    /// <param name="sha256">Its hash.</param>
    /// <param name="sizeBytes">Its size.</param>
    /// <param name="modifiedTicks">Its last-write time.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The file, or <see langword="null"/> when its name does not fit its folder and so it is not in the location.</returns>
    /// <remarks>
    /// The person's because recording it as any agent's would let that agent replace or remove something it never
    /// placed (SPEC.md §9.5). Nothing is counted: a count exists to set a limit on what agents place.
    /// </remarks>
    public async Task<StoredFile?> RecordFoundAsync(
        string conversationId,
        SharedFileName name,
        ContentHash sha256,
        long sizeBytes,
        long modifiedTicks,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(name);

        using var scope = WriteScope.Begin(_connection, transaction);

        var folder = await GetFolderAsync(conversationId, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (folder is null || !name.FitsIn(folder))
        {
            scope.Complete();
            return null;
        }

        var stamp = ExchangeRow.FormatTimestamp(nowUtc);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO shared_files
                    (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc,
                     modified_ticks)
                VALUES (@Conversation, @Key, @Name, @Size, @Sha256, NULL, @When, @When, @Ticks)
                ON CONFLICT (conversation_id, name_key) DO NOTHING;
                """,
                new
                {
                    Conversation = conversationId,
                    name.Key,
                    Name = name.Value,
                    Size = sizeBytes,
                    Sha256 = sha256.Value,
                    When = stamp,
                    Ticks = modifiedTicks,
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var found = await GetAsync(conversationId, name, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return found;
    }

    /// <summary>Brings a file's row into line with what is on disk.</summary>
    /// <param name="known">The file as the store last knew it.</param>
    /// <param name="sha256">The hash of what is on disk now.</param>
    /// <param name="sizeBytes">Its size now.</param>
    /// <param name="modifiedTicks">Its last-write time now.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when the content had changed, and notices were raised.</returns>
    /// <remarks>
    /// Change is decided by hash, not by the time or the event that prompted the look: an editor writes several
    /// times per save and a placement moves its own bytes into place, and every one of those must come to at most
    /// one change and one set of notices.
    /// </remarks>
    public async Task<bool> ReconcileAsync(
        StoredFile known,
        ContentHash sha256,
        long sizeBytes,
        long modifiedTicks,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(known);

        using var scope = WriteScope.Begin(_connection, transaction);

        var current = await GetAsync(known.File.ConversationId, known.File.Name, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {
            scope.Complete();
            return false;
        }

        if (current.File.Sha256 == sha256)
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE shared_files SET modified_ticks = @Ticks
                     WHERE conversation_id = @Conversation AND name_key = @Key;
                    """,
                    new { Ticks = modifiedTicks, Conversation = current.File.ConversationId, Key = current.NameKey },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            scope.Complete();
            return false;
        }

        await WriteContentAsync(current, sha256, sizeBytes, modifiedTicks, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        scope.Complete();
        return true;
    }

    /// <summary>Records a file renamed in its folder, keeping who placed it and when.</summary>
    /// <param name="known">The file under its old name.</param>
    /// <param name="newName">Its new name.</param>
    /// <param name="modifiedTicks">Its last-write time.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The file under its new name, or <see langword="null"/> when the new name does not fit its folder.</returns>
    /// <remarks>
    /// <para>
    /// <b>An update in place, never a delete and an insert</b>, so the placer and the placement time stay exactly as
    /// they were. Agents that fetched the file under its old name are told that name is gone (SPEC.md §9.5).
    /// </para>
    /// <para>
    /// A rename over a name already in the folder replaced that file on disk, so that row goes, with its own
    /// "gone" notices, before this one takes its name.
    /// </para>
    /// </remarks>
    public async Task<StoredFile?> RenameAsync(
        StoredFile known,
        SharedFileName newName,
        long modifiedTicks,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(newName);

        using var scope = WriteScope.Begin(_connection, transaction);

        // Read again inside the transaction. Two looks at one rename — at the name it left and the name it took — can each
        // decide it is a rename; the second must find it already done rather than take the renamed row for a file the
        // rename displaced and delete it.
        if (await GetAsync(known.File.ConversationId, known.File.Name, scope.Transaction, cancellationToken).ConfigureAwait(false) is null)
        {
            var already = await GetAsync(known.File.ConversationId, newName, scope.Transaction, cancellationToken).ConfigureAwait(false);

            scope.Complete();
            return already;
        }

        var file = known.File;

        if (!newName.FitsIn(file.Folder))
        {
            await DeleteAsync(known, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);
            scope.Complete();
            return null;
        }

        if (!string.Equals(newName.Key, known.NameKey, StringComparison.Ordinal))
        {
            var displaced = await GetAsync(file.ConversationId, newName, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);

            if (displaced is not null)
            {
                await DeleteAsync(displaced, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);
            }

            await _notices.RaiseGoneAsync(
                file.ConversationId, known.NameKey, file.Name.Value, nowUtc, forgetFetches: true,
                scope.Transaction, cancellationToken).ConfigureAwait(false);
        }

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE shared_files SET name = @Name, name_key = @NewKey, modified_ticks = @Ticks
                 WHERE conversation_id = @Conversation AND name_key = @OldKey;
                """,
                new
                {
                    Name = newName.Value,
                    NewKey = newName.Key,
                    Ticks = modifiedTicks,
                    Conversation = file.ConversationId,
                    OldKey = known.NameKey,
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var renamed = await GetAsync(file.ConversationId, newName, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return renamed;
    }

    /// <summary>Removes one file, as its placer or as the person.</summary>
    /// <param name="file">The file, as resolved.</param>
    /// <param name="caller">The agent removing it, or <see langword="null"/> for the person.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// <see cref="FileRejection.None"/> when it went; <see cref="FileRejection.NamePlacedByAnother"/> when an agent
    /// asked to remove a file it did not place; <see cref="FileRejection.NoSuchFile"/> when it had already gone.
    /// </returns>
    /// <remarks>
    /// The placer is read again inside this transaction, from the row, rather than trusted from the resolution the
    /// caller holds: a placement could have landed between the two.
    /// </remarks>
    public async Task<FileRejection> RemoveAsync(
        StoredFile file,
        AgentName? caller,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        using var scope = WriteScope.Begin(_connection, transaction);

        var current = await GetAsync(file.File.ConversationId, file.File.Name, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {
            scope.Complete();
            return FileRejection.NoSuchFile;
        }

        if (caller is not null && current.File.PlacedBy != caller)
        {
            scope.Complete();
            return FileRejection.NamePlacedByAnother;
        }

        await DeleteAsync(current, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return FileRejection.None;
    }

    /// <summary>Removes every file in the location, keeping every conversation's folder name.</summary>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The files removed.</returns>
    public async Task<IReadOnlyList<StoredFile>> RemoveAllAsync(
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = WriteScope.Begin(_connection, transaction);

        var all = await GetAllAsync(scope.Transaction, cancellationToken).ConfigureAwait(false);

        await _notices.RaiseGoneForAllAsync(nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM shared_files;",
                transaction: scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
        return all;
    }

    /// <summary>One page of the files in the location, or in one conversation.</summary>
    /// <param name="conversationId">The conversation, or <see langword="null"/> for every one.</param>
    /// <param name="pageSize">The size asked for; bounded by <see cref="FileLimits.PageSize"/>.</param>
    /// <param name="pageToken">What the previous page said to ask with, or <see langword="null"/> for the first.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ArgumentException">The page token is not one this store issued.</exception>
    /// <remarks>
    /// <para>
    /// <b>Paged by key, not by offset.</b> The token is the last conversation and name key returned, and the next
    /// page resumes after it, so a file present throughout appears exactly once however files come and go between
    /// pages. An offset would show a file twice, or skip one, every time something before it was removed.
    /// </para>
    /// <para>
    /// The overall total counts everything listed across all pages, and each conversation on the page is counted
    /// whole, so a person reading one page still sees the true size of what they are looking at.
    /// </para>
    /// </remarks>
    public async Task<SharedFilePage> ListAsync(
        string? conversationId,
        int pageSize,
        string? pageToken,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var size = FileLimits.PageSize(pageSize);
        var (afterConversation, afterKey) = DecodeToken(pageToken);
        var scoped = string.IsNullOrEmpty(conversationId) ? null : conversationId;

        var rows = (await _connection.QueryAsync<Row>(
                new CommandDefinition(
                    Select +
                    """

                     WHERE (@Conversation IS NULL OR f.conversation_id = @Conversation)
                       AND (@AfterConversation IS NULL OR (f.conversation_id, f.name_key) > (@AfterConversation, @AfterKey))
                     ORDER BY f.conversation_id, f.name_key
                     LIMIT @Take;
                    """,
                    new { Conversation = scoped, AfterConversation = afterConversation, AfterKey = afterKey, Take = size + 1 },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .AsList();

        var page = rows.Take(size).ToList();
        var next = rows.Count > size ? EncodeToken(page[^1].ConversationId, page[^1].NameKey) : null;

        var conversations = page.Select(r => r.ConversationId).Distinct(StringComparer.Ordinal).ToList();

        var totals = conversations.Count == 0
            ? []
            : (await _connection.QueryAsync<TotalRow>(
                    new CommandDefinition(
                        """
                        SELECT conversation_id AS ConversationId, count(*) AS Files, coalesce(sum(size_bytes), 0) AS Bytes
                          FROM shared_files
                         WHERE conversation_id IN @Conversations
                         GROUP BY conversation_id
                         ORDER BY conversation_id;
                        """,
                        new { Conversations = conversations },
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false))
                .Select(t => new ConversationFilesTotal(t.ConversationId, (int)t.Files, t.Bytes))
                .ToList();

        var overall = await _connection.QuerySingleAsync<TotalRow>(
            new CommandDefinition(
                """
                SELECT '' AS ConversationId, count(*) AS Files, coalesce(sum(size_bytes), 0) AS Bytes
                  FROM shared_files
                 WHERE (@Conversation IS NULL OR conversation_id = @Conversation);
                """,
                new { Conversation = scoped },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new SharedFilePage(
            [.. page.Select(r => r.ToDomain().File)],
            totals,
            (int)overall.Files,
            overall.Bytes,
            next);
    }

    /// <summary>How many placements, and how many bytes, each agent has made.</summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One count per agent that has placed anything, most placements first.</returns>
    public async Task<IReadOnlyList<FilePlacementCount>> GetPlacementsAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<PlacementRow>(
            new CommandDefinition(
                """
                SELECT agent AS Agent, placements AS Placements, bytes AS Bytes, last_utc AS LastUtc
                  FROM file_placements
                 ORDER BY placements DESC, agent;
                """,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new FilePlacementCount(
            r.Agent, r.Placements, r.Bytes, ExchangeRow.ParseTimestamp(r.LastUtc)))];
    }

    private async Task<PlaceResult> ReplaceAsync(
        StoredFile existing,
        ContentHash sha256,
        long sizeBytes,
        AgentName placedBy,
        long modifiedTicks,
        DateTimeOffset nowUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (existing.File.PlacedBy != placedBy)
        {
            return new PlaceResult(PlaceOutcome.PlacedByAnother, existing.File, null, 0);
        }

        if (existing.File.Sha256 == sha256)
        {
            return new PlaceResult(PlaceOutcome.AlreadyCurrent, existing.File, null, 0);
        }

        await WriteContentAsync(existing, sha256, sizeBytes, modifiedTicks, nowUtc, transaction, cancellationToken)
            .ConfigureAwait(false);

        await CountPlacementAsync(placedBy, sizeBytes, nowUtc, transaction, cancellationToken).ConfigureAwait(false);

        var replaced = await GetAsync(existing.File.ConversationId, existing.File.Name, transaction, cancellationToken)
            .ConfigureAwait(false);

        return new PlaceResult(PlaceOutcome.Replaced, replaced!.File, existing.File.Sha256, 0);
    }

    private async Task WriteContentAsync(
        StoredFile file,
        ContentHash sha256,
        long sizeBytes,
        long modifiedTicks,
        DateTimeOffset nowUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE shared_files
                   SET sha256 = @Sha256, size_bytes = @Size, changed_utc = @When, modified_ticks = @Ticks
                 WHERE conversation_id = @Conversation AND name_key = @Key;
                """,
                new
                {
                    Sha256 = sha256.Value,
                    Size = sizeBytes,
                    When = ExchangeRow.FormatTimestamp(nowUtc),
                    Ticks = modifiedTicks,
                    Conversation = file.File.ConversationId,
                    Key = file.NameKey,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await _notices.RaiseChangedAsync(
            file.File.ConversationId, file.NameKey, file.File.Name.Value, sha256, sizeBytes, nowUtc,
            transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteAsync(
        StoredFile file,
        DateTimeOffset nowUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await _notices.RaiseGoneAsync(
            file.File.ConversationId, file.NameKey, file.File.Name.Value, nowUtc, forgetFetches: true,
            transaction, cancellationToken).ConfigureAwait(false);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM shared_files WHERE conversation_id = @Conversation AND name_key = @Key;",
                new { Conversation = file.File.ConversationId, Key = file.NameKey },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private Task CountPlacementAsync(
        AgentName agent,
        long bytes,
        DateTimeOffset nowUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO file_placements (agent, placements, bytes, last_utc)
                VALUES (@Agent, 1, @Bytes, @When)
                ON CONFLICT (agent) DO UPDATE
                   SET placements = placements + 1, bytes = bytes + excluded.bytes, last_utc = excluded.last_utc;
                """,
                new { Agent = agent.Value, Bytes = bytes, When = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken));

    private static string EncodeToken(string conversationId, string nameKey) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(conversationId + TokenSeparator + nameKey));

    private static (string? Conversation, string? Key) DecodeToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return (null, null);
        }

        string text;

        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException invalid)
        {
            throw new ArgumentException("That page token was not issued by this store.", nameof(token), invalid);
        }

        var separator = text.IndexOf(TokenSeparator, StringComparison.Ordinal);

        return separator <= 0
            ? throw new ArgumentException("That page token was not issued by this store.", nameof(token))
            : (text[..separator], text[(separator + 1)..]);
    }

    private sealed class Row
    {
        public string ConversationId { get; init; } = "";

        public string Folder { get; init; } = "";

        public string Name { get; init; } = "";

        public string NameKey { get; init; } = "";

        public long SizeBytes { get; init; }

        public string Sha256 { get; init; } = "";

        public string? PlacedBy { get; init; }

        public string PlacedUtc { get; init; } = "";

        public string ChangedUtc { get; init; } = "";

        public long ModifiedTicks { get; init; }

        public StoredFile ToDomain() =>
            new(
                new SharedFile(
                    ConversationId,
                    Folder,
                    SharedFileName.Create(Name),
                    SizeBytes,
                    ContentHash.Parse(Sha256),
                    PlacedBy is null ? null : AgentName.Create(PlacedBy),
                    ExchangeRow.ParseTimestamp(PlacedUtc),
                    ExchangeRow.ParseTimestamp(ChangedUtc)),
                NameKey,
                ModifiedTicks);
    }

    private sealed class TotalRow
    {
        public string ConversationId { get; init; } = "";

        public long Files { get; init; }

        public long Bytes { get; init; }
    }

    private sealed class PlacementRow
    {
        public string Agent { get; init; } = "";

        public long Placements { get; init; }

        public long Bytes { get; init; }

        public string LastUtc { get; init; } = "";
    }
}
