using System.Data;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Wire = Parley.Transport;

namespace Parley.Server;

/// <summary>
/// The file operations of SPEC.md §10.1 and §10.2, over the store, the location on disk and the runtime's gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bytes move outside the gate; rows change inside it.</b> Every write to the store goes through the runtime's
/// one gate, and a hundred megabytes streamed while holding it would stall every other agent's every call for the
/// length of the upload. So a placement streams into staging before it asks for the gate, and a fetch streams after
/// it has let go — and each takes the gate only for the few statements that decide and record what happened.
/// </para>
/// <para>
/// <b>A file's turn is taken before the gate, never inside it.</b> A change waits for the downloads in progress to
/// finish, and a download does not need the gate while it streams; waiting for one while holding the gate would
/// stall everybody for however long the slowest download ran. The order is always turn, then gate, so no call can
/// hold one while waiting for the other.
/// </para>
/// <para>
/// <b>What is decided before streaming is decided again after.</b> A placement is refused early when it could never
/// succeed — a closed conversation, another agent's name — so an agent is not made to upload a hundred megabytes to
/// be told no. But the conversation can close and the name can be taken while the bytes arrive, so the same checks
/// run again inside the transaction that commits.
/// </para>
/// <para>
/// Every operation is on behalf of an attached agent or of the person, and never takes a sender: the placer and the
/// fetcher are the name the stream attached under (§16).
/// </para>
/// </remarks>
internal sealed class FileWork(BrokerRuntime runtime)
{
    private readonly BrokerRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>Places a file, streamed as a header and then pieces.</summary>
    /// <param name="frames">The client's stream.</param>
    /// <param name="cancellationToken">Cancels the placement; nothing is left behind.</param>
    /// <returns>What the placement came to.</returns>
    public async Task<Wire.PlaceFileReply> PlaceAsync(
        IAsyncStreamReader<Wire.PlaceFileFrame> frames,
        CancellationToken cancellationToken)
    {
        if (!await frames.MoveNext(cancellationToken).ConfigureAwait(false)
            || frames.Current.FrameCase != Wire.PlaceFileFrame.FrameOneofCase.Header)
        {
            throw Invalid("A placement begins with a header naming the file.");
        }

        var header = frames.Current.Header;
        var agent = Name(header.Agent);

        RequireAttached(agent);

        // Renamed here and nowhere else (SPEC.md §9.5). An adapter shows an agent the same spelling before it sends, but
        // the broker must not trust that it did, and a second implementation's adapter may not.
        var safe = SharedFileName.Safe(header.Name);
        var problem = SharedFileName.Check(safe);

        if (problem != SharedFileNameProblem.None)
        {
            return new Wire.PlaceFileReply
            {
                Rejection = Conversions.Out(Rejection(problem)),
                Name = header.Name,
                Detail = SharedFileName.Describe(problem)
            };
        }

        var name = SharedFileName.Create(safe);
        var requested = string.Equals(safe, header.Name, StringComparison.Ordinal) ? null : header.Name;
        var named = Blank(header.ConversationId);

        // The schema's rule, applied before anything is looked up or staged, so an id the store would refuse never
        // reaches it and does not come back as a fault in the broker.
        if (named is not null && !IsConversationId(named))
        {
            return new Wire.PlaceFileReply
            {
                Rejection = Wire.FileRejection.ConversationNotAssignable,
                Name = header.Name,
                ConversationId = named,
                Detail = $"'{named}' cannot be a conversation id: use up to 64 characters of letters, digits, '.', '_' and '-', with no '..'."
            };
        }

        var before = await _runtime.UseConnectionAsync(
            (connection, _, ct) => LookAsync(connection, agent, named, name, transaction: null, ct),
            cancellationToken).ConfigureAwait(false);

        if (before.Refusal is { } early)
        {
            return early;
        }

        if (requested is not null && before.Existing is { } clash)
        {
            return SafeNameTaken(requested, name, clash.File);
        }

        if (before.Existing is { } taken && taken.File.PlacedBy != agent)
        {
            return PlacedByAnother(taken.File);
        }

        // The conversation the broker chose for a sender that named none. Named explicitly from here on, because a
        // second assignment would choose a second, different identifier for the same placement.
        var conversation = before.ConversationId!;

        FileLocationStore.StagedFile staged;

        try
        {
            staged = _runtime.Files.Stage();
        }
        catch (IOException full) when (FileLocationStore.IsNoSpace(full))
        {
            return NoSpace(conversation, name);
        }

        await using (staged.ConfigureAwait(false))
        {
            try
            {
                while (await frames.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    var frame = frames.Current;

                    if (frame.FrameCase != Wire.PlaceFileFrame.FrameOneofCase.Chunk)
                    {
                        throw Invalid("A placement has one header, and it comes first.");
                    }

                    if (frame.Chunk.Length > FileLimits.ChunkBytes)
                    {
                        throw Invalid($"A piece of a placement is at most {FileLimits.ChunkBytes} bytes.");
                    }

                    await staged.WriteAsync(frame.Chunk.Memory, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (IOException full) when (FileLocationStore.IsNoSpace(full))
            {
                return NoSpace(conversation, name);
            }

            var sha256 = await staged.FinishAsync(cancellationToken).ConfigureAwait(false);
            var folder = before.Folder;

            // The turn is taken only on a file that exists, and only one of the agent's own. A new name in a folder that
            // exists has no download to wait for; a turn taken on it anyway waited on a download of a file another agent
            // placed meanwhile, made that download overdue, and was then refused.
            var turnOn = before.Existing is null ? null : folder;

            // Twice at most in practice: a file that appeared under this name while the bytes arrived needs its turn
            // taken before it can be replaced, and the second attempt holds it.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var turn = folder is null
                    ? await _runtime.Files.FirstPlacementAsync(conversation, cancellationToken).ConfigureAwait(false)
                    : turnOn is null
                        ? null
                        : await _runtime.Files.ChangeAsync(turnOn, name, cancellationToken).ConfigureAwait(false);

                using (turn)
                {
                    var heldFolder = turnOn;

                    Committed committed;

                    try
                    {
                        committed = await _runtime.UseConnectionAsync(
                            (connection, now, ct) => CommitAsync(
                                connection, agent, conversation, name, requested, staged, sha256, heldFolder, now, ct),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (FileLocationStore.NotItsOwnFolderException)
                    {
                        // Rolled back: nothing recorded, nothing written into the folder the name reaches.
                        throw new RpcException(new Status(
                            StatusCode.FailedPrecondition,
                            "This conversation's folder is another folder's short name on the broker's disk, so nothing "
                            + "was written. Tell the person running the deployment."));
                    }

                    if (committed.Reply is { } reply)
                    {
                        return reply;
                    }

                    folder = turnOn = committed.NeedsTurnOn;
                }
            }

            throw new RpcException(new Status(
                StatusCode.Aborted, "The file kept changing while it was being placed, and nothing was written. Place it again."));
        }
    }

    /// <summary>How many files in the folders listed have names §9.5 does not accept, and so are not listed.</summary>
    /// <param name="folders">Every recorded folder.</param>
    /// <param name="scope">The conversation listed, or <see langword="null"/> for all of them.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// <para>
    /// The person's alone. Such a file is never listed, fetched or opened, and the broker does not rename a file a person
    /// put on their own disk — so without this count, one copied in as <c>Meeting notes.md</c> is simply absent from
    /// every surface, with nothing to say why. A count rather than the names, because a name that breaks the rules is
    /// exactly the text nothing has checked. Agents are not told: the files are not theirs, and not in the location.
    /// </para>
    /// <para>
    /// A folder that cannot be read counts nothing rather than failing the list, which answers a different question.
    /// </para>
    /// </remarks>
    private int NotListed(IReadOnlyList<(string ConversationId, string Folder)> folders, string? scope)
    {
        var count = 0;

        foreach (var (conversation, folder) in folders)
        {
            if (scope is not null && !string.Equals(conversation, scope, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var directory = _runtime.Files.FolderPath(folder);

                if (Directory.Exists(directory))
                {
                    count += Directory.EnumerateFiles(directory)
                        .Count(path => SharedFileName.Check(Path.GetFileName(path)) != SharedFileNameProblem.None);
                }
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            {
            }
        }

        return count;
    }

    /// <summary>Whether a conversation id is one the store accepts.</summary>
    /// <param name="id">The id.</param>
    /// <returns><see langword="true"/> when the conversations table's CHECK would admit it.</returns>
    /// <remarks>
    /// The same rule as that CHECK (SPEC.md §9.2) and as the adapter's refusal. A copy, because the broker must not trust
    /// an adapter to have applied it and the store's own refusal arrives as an exception rather than as a sentence.
    /// </remarks>
    private static bool IsConversationId(string id) =>
        id.Length is >= 1 and <= 64
        && !id.Contains("..", StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Streams one whole content of a file, holding it for reading while it does.</summary>
    /// <param name="request">What to fetch.</param>
    /// <param name="writer">The response stream.</param>
    /// <param name="cancellationToken">Cancels the fetch and ends the hold.</param>
    /// <returns>A task that completes when the stream ends.</returns>
    public async Task FetchAsync(
        Wire.FetchFileRequest request,
        IServerStreamWriter<Wire.FetchFileFrame> writer,
        CancellationToken cancellationToken)
    {
        var agent = Name(request.Agent);

        RequireAttached(agent);

        FileLocationStore.ReadHold hold;

        // **A download that holds things up is let go.** Flow control parks a write to a client that stops reading, and
        // the hold it keeps made a destroy, purge or prune wait for good, and every placement that waits out one; a client
        // taking each frame just inside a bound on the frame kept a large file held for hours. So a write that takes no
        // frame for StallAfter ends it, and so does a change of the file or a hold over everything that has waited on it
        // for as long. Called inside the hold's using, so the throw lets the file go.
        async Task WriteOrLetGoAsync(Wire.FetchFileFrame frame, string folder, SharedFileName name)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_runtime.Files.IsOverdue(folder, name))
            {
                throw new RpcException(new Status(
                    StatusCode.DeadlineExceeded,
                    "The download was ended because the file is being changed or removed and the download was still going. Fetch it again."));
            }

            using var limit = new CancellationTokenSource();
            var writing = writer.WriteAsync(frame, cancellationToken);
            var stalled = Task.Delay(_runtime.Files.StallAfter, limit.Token);

            if (await Task.WhenAny(writing, stalled).ConfigureAwait(false) == writing)
            {
                await limit.CancelAsync().ConfigureAwait(false);
                await writing.ConfigureAwait(false);

                return;
            }

            _ = writing.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();

            throw new RpcException(new Status(
                StatusCode.DeadlineExceeded,
                "The download was not read, so it was ended and its file let go. Fetch it again."));
        }

        ContentHash? expected = null;

        if (!string.IsNullOrEmpty(request.ExpectSha256))
        {
            expected = ContentHash.TryParse(request.ExpectSha256, out var parsed)
                ? parsed
                : throw Invalid("expect_sha256 is 64 hexadecimal characters.");
        }

        var reference = new FileRef(Blank(request.ConversationId), request.Name);
        var resolution = await ResolveCurrentAsync(reference, cancellationToken).ConfigureAwait(false);

        if (resolution.Refusal is { } refusal)
        {
            await Refuse(writer, refusal, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stored = resolution.File!;
        var file = stored.File;

        if (expected is { } want && want != file.Sha256)
        {
            await Refuse(writer, FileRefusal.Of(reference, FileRejection.Changed), cancellationToken, file.Sha256)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            hold = await _runtime.Files.HoldForReadingAsync(file.Folder, file.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            await Refuse(writer, FileRefusal.Of(reference, FileRejection.NoSuchFile), cancellationToken).ConfigureAwait(false);
            return;
        }

        // **Resolved again under the hold.** A fetch that waited out a destroy found the row gone, but a file the person
        // held open kept the bytes on disk, and they were served under a header for a file that no longer exists.
        FileResolution again;

        try
        {
            again = await ResolveCurrentAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A cancelled or failed resolve lets the hold go: held, it kept every change of the file, and every destroy,
            // purge or prune, waiting for good.
            await hold.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        if (again.File is not { } still || still.File.Sha256 != file.Sha256
            || !string.Equals(still.File.Folder, file.Folder, StringComparison.Ordinal))
        {
            await hold.DisposeAsync().ConfigureAwait(false);
            await Refuse(
                    writer,
                    again.Refusal ?? FileRefusal.Of(reference, again.File is null ? FileRejection.NoSuchFile : FileRejection.Changed),
                    cancellationToken,
                    again.File?.File.Sha256)
                .ConfigureAwait(false);

            return;
        }

        await using (hold.ConfigureAwait(false))
        {
            await WriteOrLetGoAsync(new Wire.FetchFileFrame { Header = Conversions.Out(file) }, file.Folder, file.Name).ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileLimits.ChunkBytes];
            int read;

            while ((read = await hold.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);

                await WriteOrLetGoAsync(new Wire.FetchFileFrame { Chunk = ByteString.CopyFrom(buffer, 0, read) }, file.Folder, file.Name)
                    .ConfigureAwait(false);
            }

            var streamed = ContentHash.FromDigest(hash.GetHashAndReset());

            // Where the operating system cannot refuse an editor's save, a save during the stream shows here: the
            // bytes sent are not the content the header named, and the adapter is told to write nothing.
            if (streamed != file.Sha256)
            {
                await Refuse(writer, FileRefusal.Of(reference, FileRejection.Changed), cancellationToken, streamed)
                    .ConfigureAwait(false);
                return;
            }

            _runtime.Fetches.Served(agent, file.ConversationId, stored.NameKey, streamed);
        }
    }

    /// <summary>Records a fetch the adapter wrote and verified, if the broker streamed exactly that to it.</summary>
    /// <param name="request">The report.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was recorded.</returns>
    public async Task<Wire.FetchedFileReply> FetchedAsync(Wire.FetchedFileRequest request, CancellationToken cancellationToken)
    {
        var agent = Name(request.Agent);

        RequireAttached(agent);

        if (string.IsNullOrEmpty(request.ConversationId)
            || !SharedFileName.TryCreate(request.Name, out var name)
            || !ContentHash.TryParse(request.Sha256, out var sha256)
            || !_runtime.Fetches.TryConsume(agent, request.ConversationId, name.Key, sha256))
        {
            return new Wire.FetchedFileReply { Ok = false, Rejection = Wire.FileRejection.NotFetched };
        }

        try
        {
            await _runtime.UseConnectionAsync(
                async (connection, now, ct) =>
                {
                    await new FileNoticeRepository(connection).RecordFetchAsync(
                        agent, request.ConversationId, name, sha256, now, cancellationToken: ct).ConfigureAwait(false);

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException gone) when (gone.SqliteErrorCode == 19)
        {
            // The conversation was destroyed between the stream and the report: there is nothing left to notice.
            return new Wire.FetchedFileReply { Ok = false, Rejection = Wire.FileRejection.NoSuchFile };
        }

        return new Wire.FetchedFileReply { Ok = true, Rejection = Wire.FileRejection.None };
    }

    /// <summary>Lists a page of the location, for an agent or for the person.</summary>
    /// <param name="request">What to list.</param>
    /// <param name="forOperator">Whether the person is asking, which adds placement counts and needs no attachment.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page.</returns>
    public async Task<Wire.FilesReply> ListAsync(Wire.ListFilesRequest request, bool forOperator, CancellationToken cancellationToken)
    {
        if (!forOperator)
        {
            RequireAttached(Name(request.Agent));
        }

        var scope = Blank(request.ConversationId);

        await ReconcileScopeAsync(scope, cancellationToken).ConfigureAwait(false);

        try
        {
            return await _runtime.UseConnectionAsync(
                async (connection, _, ct) =>
                {
                    var files = new SharedFileRepository(connection);
                    var page = await files.ListAsync(scope, request.PageSize, Blank(request.PageToken), cancellationToken: ct)
                        .ConfigureAwait(false);

                    var reply = Conversions.Out(
                        page,
                        forOperator ? await files.GetPlacementsAsync(cancellationToken: ct).ConfigureAwait(false) : null);

                    if (forOperator)
                    {
                        reply.FilesNotListed = NotListed(
                            await files.GetFoldersAsync(cancellationToken: ct).ConfigureAwait(false), scope);
                    }

                    return reply;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException bad)
        {
            throw Invalid(bad.Message);
        }
    }

    /// <summary>Removes files: an agent's own, or any, for the person.</summary>
    /// <param name="request">The files.</param>
    /// <param name="forOperator">Whether the person is removing, which removes whoever placed them.</param>
    /// <param name="cancellationToken">Cancels the removal; files already removed stay removed.</param>
    /// <returns>What was removed and what was refused.</returns>
    public async Task<Wire.RemoveFilesReply> RemoveAsync(
        Wire.RemoveFilesRequest request,
        bool forOperator,
        CancellationToken cancellationToken)
    {
        AgentName? caller = null;

        if (!forOperator)
        {
            var agent = Name(request.Agent);
            RequireAttached(agent);
            caller = agent;
        }

        var reply = new Wire.RemoveFilesReply { Rejection = Wire.FileRejection.None };

        if (request.Files.Count > FileLimits.NamesPerRemoval)
        {
            reply.Rejection = Wire.FileRejection.TooManyNames;
            return reply;
        }

        foreach (var named in request.Files)
        {
            var reference = Conversions.In(named);

            var resolution = await _runtime.UseConnectionAsync(
                (connection, _, ct) => new SharedFileRepository(connection).ResolveAsync(reference, cancellationToken: ct),
                cancellationToken).ConfigureAwait(false);

            if (resolution.Refusal is { } refused)
            {
                reply.Refused.Add(Refused(refused));
                continue;
            }

            var stored = resolution.File!;
            var file = stored.File;

            // Refused before the turn is sought, because the turn waits on the file's downloads and, waiting, makes them
            // overdue: a removal another agent may not make would otherwise end them. Asked again under the turn.
            if (caller is not null && file.PlacedBy != caller)
            {
                reply.Refused.Add(Refused(new FileRefusal(reference, FileRejection.NamePlacedByAnother, file.PlacedBy?.Value, [], 0)));
                continue;
            }

            using (await _runtime.Files.ChangeAsync(file.Folder, file.Name, cancellationToken).ConfigureAwait(false))
            {
                var rejection = await _runtime.UseConnectionAsync(
                    (connection, now, ct) => new SharedFileRepository(connection).RemoveAsync(stored, caller, now, cancellationToken: ct),
                    cancellationToken).ConfigureAwait(false);

                if (rejection == FileRejection.None)
                {
                    _runtime.Files.Delete(file.Folder, file.Name);
                    reply.Removed.Add(Conversions.Out(new FileRef(file.ConversationId, file.Name.Value)));
                    reply.BytesFreed += file.SizeBytes;
                }
                else
                {
                    reply.Refused.Add(Refused(new FileRefusal(
                        reference,
                        rejection,
                        rejection == FileRejection.NamePlacedByAnother ? file.PlacedBy?.Value : null,
                        [],
                        0)));
                }
            }
        }

        return reply;
    }

    /// <summary>Removes every file in the location, for the person.</summary>
    /// <param name="cancellationToken">Cancels waiting for downloads to finish.</param>
    /// <returns>How many went, and how much space.</returns>
    public async Task<Wire.PruneFilesReply> PruneAsync(CancellationToken cancellationToken)
    {
        using (await _runtime.Files.ChangeEverythingAsync(cancellationToken).ConfigureAwait(false))
        {
            var removed = await _runtime.UseConnectionAsync(
                (connection, now, ct) => new SharedFileRepository(connection).RemoveAllAsync(now, cancellationToken: ct),
                cancellationToken).ConfigureAwait(false);

            foreach (var stored in removed)
            {
                _runtime.Files.Delete(stored.File.Folder, stored.File.Name);
            }

            return new Wire.PruneFilesReply
            {
                FilesRemoved = removed.Count,
                BytesFreed = removed.Sum(f => f.File.SizeBytes)
            };
        }
    }

    /// <summary>Says where the location, a conversation's folder or a file is, on the broker's machine.</summary>
    /// <param name="request">What to locate.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The machine and path, or why there is none.</returns>
    public async Task<Wire.FileLocationReply> DescribeAsync(Wire.FileLocationRequest request, CancellationToken cancellationToken)
    {
        var conversation = Blank(request.ConversationId);
        var name = Blank(request.Name);

        if (name is null && conversation is null)
        {
            return Located(_runtime.Files.Root);
        }

        if (name is null)
        {
            var (folder, count) = await _runtime.UseConnectionAsync(
                async (connection, _, ct) =>
                {
                    var files = new SharedFileRepository(connection);

                    return (
                        await files.GetFolderAsync(conversation!, cancellationToken: ct).ConfigureAwait(false),
                        (await files.ListAsync(conversation, 1, null, cancellationToken: ct).ConfigureAwait(false)).TotalFiles);
                },
                cancellationToken).ConfigureAwait(false);

            return folder is null || count == 0
                ? new Wire.FileLocationReply
                {
                    Ok = false,
                    Refusal = Conversions.Out(
                        FileRefusal.Of(new FileRef(conversation, string.Empty), FileRejection.NoSuchFile),
                        "That conversation has no files.")
                }
                : Located(_runtime.Files.FolderPath(folder));
        }

        var resolution = await ResolveCurrentAsync(new FileRef(conversation, name), cancellationToken).ConfigureAwait(false);

        return resolution.Refusal is { } refusal
            ? new Wire.FileLocationReply { Ok = false, Refusal = Refused(refusal) }
            : Located(_runtime.Files.FilePath(resolution.File!.File.Folder, resolution.File.File.Name));
    }

    /// <summary>Waits for every download to finish and holds new ones back, for a destroy or a purge.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The turn; disposing it lets downloads in again.</returns>
    public Task<IDisposable> HoldEverythingAsync(CancellationToken cancellationToken) =>
        _runtime.Files.ChangeEverythingAsync(cancellationToken);

    /// <summary>Deletes the folders of conversations whose rows are gone, or marks them for the next start.</summary>
    /// <param name="folders">The folders, as they were recorded before the rows went.</param>
    public void DeleteFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            _runtime.Files.DeleteOrMark(folder);
        }
    }

    private static async Task<Look> LookAsync(
        SqliteConnection connection,
        AgentName agent,
        string? named,
        SharedFileName name,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var adapter = await new AgentRepository(connection).AdapterOfAsync(agent, transaction, cancellationToken)
            .ConfigureAwait(false);

        var assignment = await new ExchangeRepository(connection).AssignConversationAsync(
            agent,
            named,
            senderIsPerson: string.Equals(adapter, PersonSeat.Adapter, StringComparison.Ordinal),
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (assignment.Refusal is { } refused)
        {
            return new Look(NotAssignable(refused, named, name), null, false, null, null);
        }

        var files = new SharedFileRepository(connection);
        var conversation = assignment.ConversationId!;
        var folder = await files.GetFolderAsync(conversation, transaction, cancellationToken).ConfigureAwait(false);
        var existing = folder is null
            ? null
            : await files.GetAsync(conversation, name, transaction, cancellationToken).ConfigureAwait(false);

        return new Look(null, conversation, assignment.Starts, folder, existing);
    }

    private async Task<Committed> CommitAsync(
        SqliteConnection connection,
        AgentName agent,
        string conversation,
        SharedFileName name,
        string? requested,
        FileLocationStore.StagedFile staged,
        ContentHash sha256,
        string? heldFolder,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var look = await LookAsync(connection, agent, conversation, name, transaction, cancellationToken).ConfigureAwait(false);

        if (look.Refusal is { } refusal)
        {
            return new Committed(refusal, null);
        }

        // Asked again inside the transaction, because the name can be taken while the bytes arrive — and before the
        // turn is sought, since a collision is refused whoever holds the file and needs no turn to say so.
        if (requested is not null && look.Existing is { } clash)
        {
            return new Committed(SafeNameTaken(requested, name, clash.File), null);
        }

        // A file on disk that no row names yet is the person's (§9.5), and committing over it would destroy it. Only
        // the watcher records such a file, and it may not have looked yet, so the disk is asked.
        if (look.Existing is null && look.Folder is { } folder && _runtime.Files.OnDisk(folder, name) is { } unrecorded)
        {
            return new Committed(PersonsOnDisk(conversation, name, requested, unrecorded), null);
        }

        // Another's file is refused here, before any turn is sought: a turn waits on the file's downloads and, waiting,
        // makes them overdue, so a placement that was never going to land would end them.
        if (look.Existing is { } another && another.File.PlacedBy != agent)
        {
            return new Committed(PlacedByAnother(another.File), null);
        }

        // A file of this name exists now, and this attempt does not hold its turn: a download of it may be streaming,
        // and replacing it under that download is what the hold exists to prevent. Rolled back, and tried again
        // holding the turn.
        if (look.Existing is not null && !string.Equals(look.Folder, heldFolder, StringComparison.Ordinal))
        {
            return new Committed(null, look.Folder);
        }

        if (look.Starts)
        {
            await new ConversationRepository(connection).EnsureOpenAsync(conversation, nowUtc, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        var files = new SharedFileRepository(connection);

        var placed = await files.PlaceAsync(
            conversation, name, sha256, staged.SizeBytes, agent, modifiedTicks: 0, nowUtc, _runtime.Files.IsTakenOnDisk,
            transaction, cancellationToken).ConfigureAwait(false);

        if (placed.Outcome is PlaceOutcome.Placed or PlaceOutcome.Replaced)
        {
            // The stored name, not the one asked with: a replacement spelled in a different case is the same file, and
            // on a disk that tells case apart the other spelling would be a second file beside it.
            var ticks = staged.CommitTo(placed.File!.Folder, placed.File.Name);
            var stored = await files.GetAsync(conversation, placed.File.Name, transaction, cancellationToken).ConfigureAwait(false);

            await files.ReconcileAsync(stored!, sha256, staged.SizeBytes, ticks, nowUtc, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        if (placed.Outcome is PlaceOutcome.Placed or PlaceOutcome.Replaced or PlaceOutcome.AlreadyCurrent)
        {
            await new ConversationRepository(connection).JoinByPlacingAsync(conversation, agent, nowUtc, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();

        return new Committed(Reply(placed, conversation, name, requested), null);
    }

    private async Task<FileResolution> ResolveCurrentAsync(FileRef reference, CancellationToken cancellationToken)
    {
        var resolution = await _runtime.UseConnectionAsync(
            (connection, _, ct) => new SharedFileRepository(connection).ResolveAsync(reference, cancellationToken: ct),
            cancellationToken).ConfigureAwait(false);

        if (resolution.File is null)
        {
            return resolution;
        }

        var current = await ReconcileAsync(resolution.File, cancellationToken).ConfigureAwait(false);

        return current is null
            ? new FileResolution(null, FileRefusal.Of(reference, FileRejection.NoSuchFile))
            : new FileResolution(current, null);
    }

    private async Task ReconcileScopeAsync(string? conversation, CancellationToken cancellationToken)
    {
        var all = await _runtime.UseConnectionAsync(
            (connection, _, ct) => new SharedFileRepository(connection).GetAllAsync(cancellationToken: ct),
            cancellationToken).ConfigureAwait(false);

        foreach (var stored in all.Where(f => conversation is null || f.File.ConversationId == conversation))
        {
            _ = await ReconcileAsync(stored, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Brings a row into line with the file on disk before it is served (SPEC.md §9.5).</summary>
    /// <param name="stored">The row.</param>
    /// <param name="cancellationToken">Cancels the hash.</param>
    /// <returns>The row as it now stands, or <see langword="null"/> when the file is not on disk.</returns>
    /// <remarks>
    /// <para>
    /// A size or modification time that differs from the row means somebody changed the file outside Parley and the
    /// watcher has not caught up, so the file is hashed — outside the gate — and the row updated inside it. A missed
    /// event is then a delay, never a stale hash served.
    /// </para>
    /// <para>
    /// <b>A missing file is not recorded as gone here.</b> An editor saving by rename deletes and recreates, and a
    /// fetch landing between the two would otherwise remove the row and lose the placer. The watcher and the scan
    /// at start decide what is gone; this only declines to serve what is not there.
    /// </para>
    /// </remarks>
    private async Task<StoredFile?> ReconcileAsync(StoredFile stored, CancellationToken cancellationToken)
    {
        var path = _runtime.Files.FilePath(stored.File.Folder, stored.File.Name);
        var info = new FileInfo(path);

        if (!info.Exists)
        {
            return null;
        }

        if (info.Length == stored.File.SizeBytes && info.LastWriteTimeUtc.Ticks == stored.ModifiedTicks)
        {
            return stored;
        }

        if (await FileLocationStore.HashAsync(path, cancellationToken).ConfigureAwait(false) is not { } measured)
        {
            return stored;
        }

        return await _runtime.UseConnectionAsync(
            async (connection, now, ct) =>
            {
                var files = new SharedFileRepository(connection);

                _ = await files.ReconcileAsync(
                    stored, measured.Sha256, measured.SizeBytes, measured.ModifiedTicks, now, cancellationToken: ct)
                    .ConfigureAwait(false);

                return await files.GetAsync(stored.File.ConversationId, stored.File.Name, cancellationToken: ct)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static Wire.PlaceFileReply Reply(PlaceResult placed, string conversation, SharedFileName asked, string? requested) => placed.Outcome switch
    {
        // A file on disk that no row named yet is refused here rather than in the look, and it is still a collision.
        PlaceOutcome.PlacedByAnother when requested is not null => SafeNameTaken(requested, asked, placed.File!),
        PlaceOutcome.PlacedByAnother => PlacedByAnother(placed.File!),
        PlaceOutcome.DoesNotFit => new Wire.PlaceFileReply
        {
            Outcome = Wire.PlaceOutcome.DoesNotFit,
            Rejection = Wire.FileRejection.BadName,
            ConversationId = conversation,
            Name = asked.Value,
            CharactersLeft = placed.CharactersLeft,
            Detail =
                $"A file's folder and name together are at most {SharedFileName.MaxPathCharacters} characters, so a " +
                $"name in this conversation can be at most {placed.CharactersLeft} characters."
        },
        _ => new Wire.PlaceFileReply
        {
            Outcome = Conversions.Out(placed.Outcome),
            Rejection = Wire.FileRejection.None,
            ConversationId = conversation,
            Name = placed.File!.Name.Value,
            SizeBytes = placed.File.SizeBytes,
            Sha256 = placed.File.Sha256.Value,
            PreviousSha256 = placed.Previous?.Value ?? string.Empty,
            RequestedName = requested ?? string.Empty
        }
    };

    private static Wire.PlaceFileReply PersonsOnDisk(string conversation, SharedFileName name, string? requested, string onDisk) =>
        requested is null
            ? new()
            {
                Outcome = Wire.PlaceOutcome.PlacedByAnother,
                Rejection = Wire.FileRejection.NamePlacedByAnother,
                ConversationId = conversation,
                Name = name.Value,
                Detail = PlacerSentence(null)
            }
            : new()
            {
                Rejection = Wire.FileRejection.SafeNameTaken,
                ConversationId = conversation,
                Name = name.Value,
                RequestedName = requested,
                Detail = SharedFileName.SafeNameTaken(requested, name.Value, onDisk)
            };

    private static Wire.PlaceFileReply SafeNameTaken(string requested, SharedFileName safe, SharedFile existing) => new()
    {
        Rejection = Wire.FileRejection.SafeNameTaken,
        ConversationId = existing.ConversationId,
        Name = safe.Value,
        RequestedName = requested,
        PlacedBy = existing.PlacedBy?.Value ?? string.Empty,
        Detail = SharedFileName.SafeNameTaken(requested, safe.Value, existing.Name.Value)
    };

    private static Wire.PlaceFileReply PlacedByAnother(SharedFile file) => new()
    {
        Outcome = Wire.PlaceOutcome.PlacedByAnother,
        Rejection = Wire.FileRejection.NamePlacedByAnother,
        ConversationId = file.ConversationId,
        Name = file.Name.Value,
        PlacedBy = file.PlacedBy?.Value ?? string.Empty,
        Detail = PlacerSentence(file.PlacedBy?.Value)
    };

    private static Wire.PlaceFileReply NotAssignable(OpenOutcome refused, string? named, SharedFileName name)
    {
        var closed = refused.Rejection == RequestRejection.ConversationClosed;

        var reply = new Wire.PlaceFileReply
        {
            Rejection = closed ? Wire.FileRejection.ConversationClosed : Wire.FileRejection.ConversationNotAssignable,
            ConversationId = named ?? string.Empty,
            Name = name.Value,
            Detail = closed
                ? "That conversation is closed, so nothing more can be placed in it. Work worth resuming is new work."
                : refused.Assignable.Count > 0
                    ? "You hold requests in more than one conversation, or in a different one: name one of those listed."
                    : "That conversation is not one you are part of, and not one you can start: it exists without you, "
                        + "or its name is taken. Leave conversation_id out to place the file in a new conversation."
        };

        reply.AssignableConversations.AddRange(refused.Assignable);
        return reply;
    }

    private static Wire.PlaceFileReply NoSpace(string conversation, SharedFileName name) => new()
    {
        Rejection = Wire.FileRejection.NoSpace,
        ConversationId = conversation,
        Name = name.Value,
        Detail =
            "The broker's disk is full, so the file was not placed and nothing partial was kept. Tell the person " +
            "running the deployment."
    };

    private static Wire.FileRefusal Refused(FileRefusal refusal, ContentHash? sha256Now = null) =>
        Conversions.Out(refusal, Detail(refusal, sha256Now), sha256Now);

    private static Task Refuse(
        IServerStreamWriter<Wire.FetchFileFrame> writer,
        FileRefusal refusal,
        CancellationToken cancellationToken,
        ContentHash? sha256Now = null) =>
        writer.WriteAsync(new Wire.FetchFileFrame { Refusal = Refused(refusal, sha256Now) }, cancellationToken);

    private static string Detail(FileRefusal refusal, ContentHash? sha256Now) => refusal.Rejection switch
    {
        FileRejection.NoSuchFile => refusal.File.ConversationId is null
            ? "No file of that name is in the location."
            : "No file of that name is in that conversation.",
        FileRejection.AmbiguousName =>
            $"'{refusal.File.Name}' is in more than one conversation: {string.Join(", ", refusal.Conversations)}" +
            (refusal.ConversationsMore > 0 ? $", and {refusal.ConversationsMore} more" : string.Empty) +
            ". Nothing was done; call again naming the conversation you want.",
        FileRejection.BadName or FileRejection.ExtensionNotAllowed => SharedFileName.DescribeLookup(refusal.File.Name),
        FileRejection.NamePlacedByAnother => PlacerSentence(refusal.PlacedBy),
        FileRejection.Changed =>
            $"The file is not the content expected: the location holds {sha256Now?.Value ?? "different content"}. " +
            "Nothing was written. Fetch it again, and check with whoever named the hash if it matters.",
        _ => "The file operation was refused."
    };

    private static string PlacerSentence(string? placedBy) =>
        placedBy is null
            ? "The person running the deployment put that file there, so no agent may replace or remove it."
            : $"{placedBy} placed that file, and only the agent that placed a file may replace or remove it.";

    private static FileRejection Rejection(SharedFileNameProblem problem) =>
        problem == SharedFileNameProblem.ExtensionNotAllowed ? FileRejection.ExtensionNotAllowed : FileRejection.BadName;

    private static Wire.FileLocationReply Located(string path) =>
        new() { Ok = true, Machine = MachineName.Current, Path = path };

    private void RequireAttached(AgentName agent)
    {
        if (_runtime.SessionsFor(agent) == 0)
        {
            var refused = new NotAttachedException(agent);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, refused.Message, refused));
        }
    }

    private static RpcException Invalid(string why) => new(new Status(StatusCode.InvalidArgument, why));

    private static AgentName Name(string value) =>
        AgentName.TryCreate(value, out var name)
            ? name
            : throw Invalid($"'{value}' is not an agent name. Names are role@machine (SPEC.md §4.1.1).");

    private static string? Blank(string value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed record Look(
        Wire.PlaceFileReply? Refusal,
        string? ConversationId,
        bool Starts,
        string? Folder,
        StoredFile? Existing);

    private sealed record Committed(Wire.PlaceFileReply? Reply, string? NeedsTurnOn);
}
