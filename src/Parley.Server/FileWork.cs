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

    /// <summary>Places a file for the attached agent the header names, streamed as a header and then pieces.</summary>
    /// <param name="frames">The client's stream.</param>
    /// <param name="cancellationToken">Cancels the placement; nothing is left behind.</param>
    /// <returns>What the placement came to.</returns>
    public async Task<Wire.PlaceFileReply> PlaceAsync(
        IAsyncStreamReader<Wire.PlaceFileFrame> frames,
        CancellationToken cancellationToken)
    {
        var header = await HeaderAsync(frames, cancellationToken).ConfigureAwait(false);
        var agent = Name(header.Agent);

        RequireAttached(agent);

        return await PlaceAsync(header, frames, agent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Places a file as the person, into a conversation that exists (SPEC.md §10.2 <c>place_persons_file</c>).</summary>
    /// <param name="frames">The surface's stream; the header names no agent.</param>
    /// <param name="cancellationToken">Cancels the placement; nothing is left behind.</param>
    /// <returns>What the placement came to.</returns>
    /// <remarks>
    /// The person's file is recorded with no placer, which is what "the person's" is in the store, and is counted
    /// against nobody. It joins nobody to the conversation: the person reads every conversation already.
    /// </remarks>
    public async Task<Wire.PlaceFileReply> PlaceAsPersonAsync(
        IAsyncStreamReader<Wire.PlaceFileFrame> frames,
        CancellationToken cancellationToken)
    {
        var header = await HeaderAsync(frames, cancellationToken).ConfigureAwait(false);

        if (Blank(header.ConversationId) is null)
        {
            throw Invalid("A placement by the person names the conversation it goes in.");
        }

        return await PlaceAsync(header, frames, placer: null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Wire.PlaceFileHeader> HeaderAsync(
        IAsyncStreamReader<Wire.PlaceFileFrame> frames,
        CancellationToken cancellationToken)
    {
        if (!await frames.MoveNext(cancellationToken).ConfigureAwait(false)
            || frames.Current.FrameCase != Wire.PlaceFileFrame.FrameOneofCase.Header)
        {
            throw Invalid("A placement begins with a header naming the file.");
        }

        return frames.Current.Header;
    }

    private async Task<Wire.PlaceFileReply> PlaceAsync(
        Wire.PlaceFileHeader header,
        IAsyncStreamReader<Wire.PlaceFileFrame> frames,
        AgentName? placer,
        CancellationToken cancellationToken)
    {
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
            (connection, _, ct) => LookAsync(connection, placer, named, name, transaction: null, ct),
            cancellationToken).ConfigureAwait(false);

        if (before.Refusal is { } early)
        {
            return early;
        }

        if (requested is not null && before.Existing is { } clash)
        {
            return SafeNameTaken(requested, name, clash.File);
        }

        if (before.Existing is { } taken && taken.File.PlacedBy != placer)
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

            // **A placement writes only a name nothing holds**, so it never waits on a download: no file it could write
            // over is being fetched (SPEC.md §9.5). A conversation's first file makes its folder, which only one
            // placement may do at a time.
            using var turn = before.Folder is null
                ? await _runtime.Files.FirstPlacementAsync(conversation, cancellationToken).ConfigureAwait(false)
                : null;

            try
            {
                return await _runtime.UseConnectionAsync(
                    (connection, now, ct) => CommitAsync(connection, placer, conversation, name, requested, staged, sha256, now, ct),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FileLocationStore.NotItsOwnFolderException)
            {
                // Rolled back: nothing recorded, nothing written into the folder the name reaches. A folder replaced by a
                // link is something else's doing, and the sweep this asks for deletes the link (SPEC.md §9.5).
                _runtime.Sweeper.Ask();
                throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    "This conversation's folder on the broker's disk is not its own: another folder's short name, or a "
                    + "link put there by hand. Nothing was written. Tell the person running the deployment."));
            }
        }
    }

    // A folder that cannot be read is not taken for a link; the walk passes it over.
    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Everything under a directory that is not a placed file directly in it, read one level at a time from a stack of
    // its own rather than one call a level: a tree made by hand can be deeper than the stack, and an overflow ends the
    // process. A link is one entry and never entered, since it may lead out of the location or back into itself, and a
    // level that cannot be read is passed over rather than costing the rest.
    internal static int Unplaced(string folder, ISet<string> placed)
    {
        var count = 0;
        var pending = new Stack<(string Directory, bool Top)>();
        pending.Push((folder, true));

        while (pending.TryPop(out var level))
        {
            IEnumerable<string> entries;

            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(level.Directory)];
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;

                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
                {
                    count++;
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    count++;
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, false));
                }
                else if (!level.Top || !placed.Contains(Path.GetFileName(entry)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>How many files in the folders listed nothing placed, and so are not listed (SPEC.md §9.5).</summary>
    /// <param name="folders">Every recorded folder.</param>
    /// <param name="placed">Every file placed.</param>
    /// <param name="scope">The conversation listed, or <see langword="null"/> for all of them.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// <para>
    /// The person's alone. Files enter the location only by being placed, so one copied into a folder, renamed there or
    /// named against the rules is never listed, fetched or opened, and without this count it is simply absent from every
    /// surface, with nothing to say why. A count rather than the names, because such a name is text nothing has
    /// checked. Agents are not told: the files are not theirs, and not in the location.
    /// </para>
    /// <para>
    /// A folder that cannot be read counts nothing rather than failing the list, which answers a different question.
    /// </para>
    /// </remarks>
    private int NotListed(
        IReadOnlyList<(string ConversationId, string Folder)> folders,
        IReadOnlyList<StoredFile> placed,
        string? scope)
    {
        var count = 0;

        foreach (var (conversation, folder) in folders)
        {
            if (scope is not null && !string.Equals(conversation, scope, StringComparison.Ordinal))
            {
                continue;
            }

            // The spelling placed, exactly: of two names differing only in case on a disk that tells them apart, only the
            // one placed is the file.
            var names = placed.Where(p => p.File.ConversationId == conversation)
                .Select(p => p.File.Name.Value)
                .ToHashSet(StringComparer.Ordinal);

            var directory = _runtime.Files.FolderPath(folder);

            // Every file under the folder, a subfolder copied in by hand included: a placed file is only ever directly in
            // it, so anything deeper was put there by hand, and a destroy deletes it with the folder.
            if (!Directory.Exists(directory))
            {
                continue;
            }

            // A folder that is itself a link counts as one entry, never walked, as a link inside it does.
            count += IsLink(directory) ? 1 : Unplaced(directory, names);
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

        // **A download that has stopped is let go.** Flow control parks a write to a client that stops reading, and the
        // hold it keeps made a destroy, purge or prune wait for good, and every placement that waits out one. So a write
        // that takes no frame for StallAfter ends it. A download still taking frames is never ended, however slowly it
        // goes and whatever waits on it: what waits, waits for it to finish. Called inside the hold's using, so the throw
        // lets the file go.
        async Task WriteOrLetGoAsync(Wire.FetchFileFrame frame)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Held for writing by another program, a person saving over it by hand among them, or not readable by the
            // broker at all (SPEC.md §9.5): refused in words, never as a fault in the broker, which is what the agent got.
            await writer.WriteAsync(
                new Wire.FetchFileFrame
                {
                    Refusal = Conversions.Out(
                        FileRefusal.Of(reference, FileRejection.Changed),
                        held is UnauthorizedAccessException
                            ? "The broker may not read the file on its machine, so nothing was written. Tell the person "
                              + "running the deployment."
                            : "The file is held for writing by another program on the broker's machine, so it could not be "
                              + "read and nothing was written. A placed file is never changed: if it is being written by "
                              + "hand, its placer should place the new version under a new name.",
                        null),
                },
                cancellationToken).ConfigureAwait(false);
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
            await WriteOrLetGoAsync(new Wire.FetchFileFrame { Header = Conversions.Out(file) }).ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileLimits.ChunkBytes];
            int read;

            while ((read = await hold.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);

                await WriteOrLetGoAsync(new Wire.FetchFileFrame { Chunk = ByteString.CopyFrom(buffer, 0, read) })
                    .ConfigureAwait(false);
            }

            var streamed = ContentHash.FromDigest(hash.GetHashAndReset());

            // **Bytes changed on disk are tampering** (SPEC.md §9.5). A placed file is never changed through Parley, so bytes
            // that are not the content the header named were changed by something else, before or during the stream: the
            // adapter writes nothing, and a sweep sets the file aside and tells everybody who holds it.
            if (streamed != file.Sha256)
            {
                _runtime.Sweeper.Ask();

                await writer.WriteAsync(
                    new Wire.FetchFileFrame
                    {
                        Refusal = Conversions.Out(FileRefusal.Of(reference, FileRejection.Tampered), TamperedSentence, streamed),
                    },
                    cancellationToken).ConfigureAwait(false);
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
            var (reply, folders, placed) = await _runtime.UseConnectionAsync(
                async (connection, _, ct) =>
                {
                    var files = new SharedFileRepository(connection);
                    var page = await files.ListAsync(scope, request.PageSize, Blank(request.PageToken), cancellationToken: ct)
                        .ConfigureAwait(false);

                    var reply = Conversions.Out(
                        page,
                        forOperator ? await files.GetPlacementsAsync(cancellationToken: ct).ConfigureAwait(false) : null);

                    return forOperator
                        ? (reply,
                           await files.GetFoldersAsync(cancellationToken: ct).ConfigureAwait(false),
                           await files.GetAllAsync(cancellationToken: ct).ConfigureAwait(false))
                        : (reply, [], []);
                },
                cancellationToken).ConfigureAwait(false);

            // Counted outside the connection's turn: a walk of the disk is as slow as what is on it, and every agent's call
            // waits on that turn.
            if (forOperator)
            {
                reply.FilesNotListed = NotListed(folders, placed, scope);

                // Nothing but a placement puts a file in the location, so one there unplaced was put there by something
                // else, and a sweep deletes it (SPEC.md §9.5).
                if (reply.FilesNotListed > 0)
                {
                    _runtime.Sweeper.Ask();
                }
            }

            return reply;
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

            // Refused before the turn is sought, because the turn waits for the file's downloads and holds new ones back
            // while it waits: a removal another agent may not make would otherwise stop the file being fetched. Asked
            // again under the turn.
            if (caller is not null && file.PlacedBy != caller)
            {
                reply.Refused.Add(Refused(new FileRefusal(reference, FileRejection.NamePlacedByAnother, file.PlacedBy?.Value, [], 0)));
                continue;
            }

            using (await _runtime.Files.ChangeAsync(file.Folder, file.Name, cancellationToken).ConfigureAwait(false))
            {
                // **An agent removes only the bytes it placed** (SPEC.md §9.5). A file there with another size or time was
                // changed by something other than Parley; a sweep looks at it, and quarantines it if its bytes changed.
                if (caller is not null && !_runtime.Files.HoldsTheRecordedFileOrNone(file.Folder, file.Name, (file.SizeBytes, stored.ModifiedTicks)))
                {
                    _runtime.Sweeper.Ask();
                    reply.Refused.Add(Conversions.Out(
                        FileRefusal.Of(reference, FileRejection.Tampered),
                        "The file on disk is not the one you placed under this name: something other than Parley changed "
                        + "it, so nothing was removed. The broker is checking it; if its bytes changed, it is set aside and "
                        + "you are told in your inbox.",
                        null));
                    continue;
                }

                // **The bytes go in the turn the row goes in**, which a placement's commit also holds while it moves its
                // bytes in: deleted after it, they could be a placement's of the name, made in between and told Placed.
                var rejection = await _runtime.UseConnectionAsync(
                    async (connection, now, ct) =>
                    {
                        var removed = await new SharedFileRepository(connection).RemoveAsync(stored, caller, now, cancellationToken: ct)
                            .ConfigureAwait(false);

                        if (removed == FileRejection.None)
                        {
                            _runtime.Files.Delete(file.Folder, file.Name);
                        }

                        return removed;
                    },
                    cancellationToken).ConfigureAwait(false);

                if (rejection == FileRejection.None)
                {
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
            // The bytes go in the rows' turn, as a single removal's do, so a placement of a pruned name made after the
            // rows went is never deleted with them.
            var removed = await _runtime.UseConnectionAsync(
                async (connection, now, ct) =>
                {
                    var gone = await new SharedFileRepository(connection).RemoveAllAsync(now, cancellationToken: ct)
                        .ConfigureAwait(false);

                    foreach (var stored in gone)
                    {
                        _runtime.Files.Delete(stored.File.Folder, stored.File.Name);
                    }

                    return gone;
                },
                cancellationToken).ConfigureAwait(false);

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

    /// <summary>Deletes the folders of conversations whose rows are gone; what it cannot goes at the next sweep.</summary>
    /// <param name="folders">The folders, as they were recorded before the rows went.</param>
    public void DeleteFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            _ = _runtime.Files.DeleteFolder(folder);
        }
    }

    private static async Task<Look> LookAsync(
        SqliteConnection connection,
        AgentName? placer,
        string? named,
        SharedFileName name,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        string conversation;
        var starts = false;

        if (placer is { } agent)
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

            conversation = assignment.ConversationId!;
            starts = assignment.Starts;
        }
        else
        {
            // The person places into a conversation that exists, and starts none (SPEC.md §10.2 place_persons_file).
            if (await new ConversationRepository(connection).GetAsync(named!, transaction, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return new Look(
                    new Wire.PlaceFileReply
                    {
                        Rejection = Wire.FileRejection.ConversationNotAssignable,
                        Name = name.Value,
                        ConversationId = named,
                        Detail = $"There is no conversation '{named}' to place the file in.",
                    },
                    null,
                    false,
                    null,
                    null);
            }

            conversation = named!;
        }

        var files = new SharedFileRepository(connection);
        var folder = await files.GetFolderAsync(conversation, transaction, cancellationToken).ConfigureAwait(false);
        var existing = folder is null
            ? null
            : await files.GetAsync(conversation, name, transaction, cancellationToken).ConfigureAwait(false);

        return new Look(null, conversation, starts, folder, existing);
    }

    private async Task<Wire.PlaceFileReply> CommitAsync(
        SqliteConnection connection,
        AgentName? placer,
        string conversation,
        SharedFileName name,
        string? requested,
        FileLocationStore.StagedFile staged,
        ContentHash sha256,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var look = await LookAsync(connection, placer, conversation, name, transaction, cancellationToken).ConfigureAwait(false);

        if (look.Refusal is { } refusal)
        {
            return refusal;
        }

        // Asked again inside the transaction, because the name can be taken while the bytes arrive.
        if (requested is not null && look.Existing is { } clash)
        {
            return SafeNameTaken(requested, name, clash.File);
        }

        // A file on disk under a name nothing placed was put there by hand (SPEC.md §9.5), and a placement never writes
        // over it.
        if (look.Existing is null && look.Folder is { } folder && _runtime.Files.OnDisk(folder, name) is { } unrecorded)
        {
            _runtime.Sweeper.Ask();
            return PersonsOnDisk(conversation, name, requested, unrecorded);
        }

        if (look.Starts)
        {
            await new ConversationRepository(connection).EnsureOpenAsync(conversation, nowUtc, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        var files = new SharedFileRepository(connection);

        var placed = await files.PlaceAsync(
            conversation, name, sha256, staged.SizeBytes, placer, modifiedTicks: 0, nowUtc, _runtime.Files.IsTakenOnDisk,
            transaction, cancellationToken).ConfigureAwait(false);

        // **A recorded file gone from disk is written again, though the hash is the row's.** The row stands until a list,
        // a fetch or a sweep finds the file missing, and the same bytes placed in that time were answered as already
        // current with nothing on disk.
        var write = placed.Outcome is PlaceOutcome.Placed
                    || (placed.Outcome is PlaceOutcome.AlreadyCurrent
                        && !File.Exists(_runtime.Files.FilePath(placed.File!.Folder, placed.File.Name)));

        if (write)
        {
            // The stored name, not the one asked with: on a disk that tells case apart the other spelling would be a
            // second file beside it.
            var ticks = staged.CommitTo(placed.File!.Folder, placed.File.Name);
            var stored = await files.GetAsync(conversation, placed.File.Name, transaction, cancellationToken).ConfigureAwait(false);

            await files.RecordPlacedTimeAsync(stored!, ticks, transaction, cancellationToken).ConfigureAwait(false);
        }

        if (placer is { } agent && placed.Outcome is PlaceOutcome.Placed or PlaceOutcome.AlreadyCurrent)
        {
            await new ConversationRepository(connection).JoinByPlacingAsync(conversation, agent, nowUtc, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();

        return Reply(placed, conversation, name, requested);
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

    /// <summary>Records a file gone when it is no longer on disk, before it is listed or served (SPEC.md §9.5).</summary>
    /// <param name="stored">The row.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The row, or <see langword="null"/> when the file was not on disk and is now recorded gone.</returns>
    /// <remarks>
    /// Nothing is hashed here: bytes changed on disk are caught by the fetch, which hashes what it streams, and by the
    /// sweep. A file something else deleted is gone, as a removal is; its fetchers and its placer are told, and the
    /// sweep this asks for looks at the rest of the location.
    /// </remarks>
    private async Task<StoredFile?> ReconcileAsync(StoredFile stored, CancellationToken cancellationToken)
    {
        // A folder that cannot be read says nothing about its files, which stay as recorded.
        if (_runtime.Files.IsOnDisk(stored.File.Folder, stored.File.Name) != false)
        {
            return stored;
        }

        // Asked again inside the connection's turn, which a placement's commit also holds while it moves its bytes in: a
        // placement of the name between the two would otherwise have its row recorded gone with its file on disk. And
        // only the file resolved goes; one placed under the name since is not it.
        var gone = await _runtime.UseConnectionAsync(
            async (connection, now, ct) =>
                _runtime.Files.IsOnDisk(stored.File.Folder, stored.File.Name) == false
                && await new SharedFileRepository(connection).RemoveMissingAsync(stored, now, cancellationToken: ct)
                    .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        // Nothing but Parley removes a placed file, so one gone from disk was removed by something else: a sweep looks at
        // the rest of the location too (SPEC.md §9.5).
        if (gone)
        {
            _runtime.Sweeper.Ask();
        }

        return gone ? null : stored;
    }

    private static Wire.PlaceFileReply Reply(PlaceResult placed, string conversation, SharedFileName asked, string? requested) => placed.Outcome switch
    {
        // A file on disk that no row named yet is refused here rather than in the look, and it is still a collision.
        PlaceOutcome.PlacedByAnother when requested is not null => SafeNameTaken(requested, asked, placed.File!),
        PlaceOutcome.PlacedByAnother => PlacedByAnother(placed.File!),
        PlaceOutcome.HoldsOtherContent => new Wire.PlaceFileReply
        {
            Outcome = Wire.PlaceOutcome.HoldsOtherContent,
            Rejection = Wire.FileRejection.NameHoldsOtherContent,
            ConversationId = conversation,
            Name = placed.File!.Name.Value,
            Sha256 = placed.File.Sha256.Value,
            Detail =
                $"'{placed.File.Name.Value}' is already here with other content, and a placed file is never changed. "
                + "Place the new version under a new name, and remove this one when nobody needs it."
        },
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
                Detail = UnplacedSentence
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
            "Nothing was written. A placed file is never changed, so check with whoever named the hash which file they meant.",
        _ => "The file operation was refused."
    };

    private static string PlacerSentence(string? placedBy) =>
        placedBy is null
            ? "The person running the deployment placed that file, so nothing was done: a placed file is never changed, "
              + "and no agent removes the person's. To place a file, use another name."
            : $"{placedBy} placed that file, so nothing was done: a placed file is never changed, and only its placer or "
              + "the person removes it. To place a file, use another name.";

    // A file on disk under a name nothing placed: put there by something other than Parley, never written over, and
    // deleted by the sweep this asked for.
    private const string UnplacedSentence =
        "A file nothing placed is on disk under that name, put there by something other than Parley, so nothing was "
        + "placed: a placement never writes over it. The broker is removing it; place the file again in a moment.";

    // A file whose bytes on disk are not what was placed (SPEC.md §9.5).
    private const string TamperedSentence =
        "The file was tampered with on the broker's machine: its bytes no longer match what was placed, so it is no "
        + "longer reliable and nothing was written. The broker is setting it aside; ask its placer to place it again.";

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

}
