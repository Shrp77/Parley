using System.Security.Cryptography;
using Parley.Core;

namespace Parley.Server;

/// <summary>
/// The file location on disk: its folders, the staging directory beside it, and the holds a download takes
/// (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bytes only.</b> What a file is called, who placed it and what its hash is live in the store; this is where
/// they are kept, and it knows nothing a row does not tell it. It lives in the server, the one process that owns
/// a store, because the location is that store's and nothing else may write it.
/// </para>
/// <para>
/// <b>A placement is staged beside the location and moved in when complete</b>: the
/// folder a person opens and the watcher watches never hold a half-uploaded file, and on the same disk the move
/// is one step. A staged file that is never committed is deleted, and the staging directory is emptied when the
/// broker starts, because nothing survives a broker that could have finished writing it.
/// </para>
/// <para>
/// <b>A download holds its file, and a change through Parley waits for every hold.</b> A read opens the file with
/// no write or delete sharing, so on Windows an editor's save is refused for as long as it runs. A replacement,
/// removal or prune asks for the file's gate and waits until the reads in progress finish; a read that arrives
/// while a change is waiting waits for the change, so a busy file cannot hold a change back for ever. On Linux and
/// macOS nothing refuses the editor, which is why every read is hashed against the row (§9.5).
/// </para>
/// </remarks>
public sealed class FileLocationStore : IDisposable
{
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);

    // Set, under the gates' lock, while a destroy, purge or prune holds the whole location. A file never read before has
    // no gate for that hold to enter, so its first download waits on this instead of streaming a file the hold is about
    // to delete.
    private TaskCompletionSource? _everything;

    /// <summary>How long a download may go without its client taking a frame before it is ended and its hold let go.</summary>
    /// <remarks>
    /// Only a download that has stopped is ended. One still taking frames is never ended, however long it takes or
    /// whatever waits on it, because nothing that is still moving times out (SPEC.md §9.5). Settable so a test need not
    /// wait it out.
    /// </remarks>
    internal TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(30);
    private readonly Func<string, Stream> _createStaging;
    private readonly bool _deleteOnDispose;

    /// <summary>Opens a file location.</summary>
    /// <param name="root">The location: a directory holding one folder per conversation.</param>
    /// <param name="incoming">The staging directory, beside the location and never inside it.</param>
    /// <param name="deleteOnDispose">Whether both directories are deleted when this is disposed, which only a test's temporary location wants.</param>
    /// <param name="createStaging">
    /// Opens a staging file for writing. A seam, so a test can run out of disk on demand; <see langword="null"/>
    /// opens a real file.
    /// </param>
    public FileLocationStore(
        string root,
        string incoming,
        bool deleteOnDispose = false,
        Func<string, Stream>? createStaging = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(incoming);

        Root = Path.GetFullPath(root);
        Incoming = Path.GetFullPath(incoming);
        _deleteOnDispose = deleteOnDispose;
        _createStaging = createStaging
            ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileLimits.ChunkBytes));
    }

    /// <summary>The location's absolute path.</summary>
    public string Root { get; }

    /// <summary>The staging directory's absolute path.</summary>
    public string Incoming { get; }

    /// <summary>Whether a disk error means there is no space left.</summary>
    /// <param name="error">The error.</param>
    /// <returns><see langword="true"/> for a full disk on Windows, Linux or macOS.</returns>
    public static bool IsNoSpace(IOException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL on Windows; ENOSPC elsewhere.
        return (error.HResult & 0xFFFF) is 0x70 or 0x27 || error.HResult == 28;
    }

    /// <summary>Hashes a file on disk without holding it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its hash, size and last-write time, or <see langword="null"/> when it cannot be read.</returns>
    public static async Task<(ContentHash Sha256, long SizeBytes, long ModifiedTicks)?> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var modified = File.GetLastWriteTimeUtc(path).Ticks;

            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileLimits.ChunkBytes,
                useAsync: true);

            await using var _ = stream.ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileLimits.ChunkBytes];
            long size = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                size += read;
            }

            return (ContentHash.FromDigest(hash.GetHashAndReset()), size, modified);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Makes sure the location and the staging directory exist, and empties the staging directory.</summary>
    /// <remarks>Called when a broker starts: nothing staged by a previous one can still be finished.</remarks>
    public void Prepare()
    {
        Directory.CreateDirectory(Root);

        if (Directory.Exists(Incoming))
        {
            foreach (var leftover in Directory.EnumerateFiles(Incoming))
            {
                TryDelete(leftover);
            }
        }

        Directory.CreateDirectory(Incoming);
    }

    /// <summary>The file a destroy or a purge leaves in a folder it could not delete, so a later start deletes it.</summary>
    internal const string LeftBehind = ".parley-pending-removal";

    /// <summary>Deletes a destroyed conversation's folder, or marks it to be deleted when the broker next starts.</summary>
    /// <param name="folder">The folder name, as the store recorded it before its row went.</param>
    /// <remarks>
    /// Marked rather than forgotten: a destroy or a purge commits before it deletes, so a folder held open when it ran
    /// (an editor with a file locked) kept a conversation's files on disk for good, though the operator was told they
    /// went. Only a marked folder is deleted at a start, because the location also holds what a person put there by hand
    /// and what a store restored from a backup no longer records (SPEC.md §9.5); neither is Parley's to delete.
    /// </remarks>
    public void DeleteOrMark(string folder)
    {
        if (DeleteFolder(folder))
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(FolderPath(folder), LeftBehind), string.Empty);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done from here: the folder stays, as it did before there was a mark.
        }
    }

    /// <summary>Deletes every folder a destroy or a purge marked and the store no longer records.</summary>
    /// <param name="recorded">The folder names the store records.</param>
    /// <remarks>
    /// Called when a broker starts, under the store's lock and before it serves, so no placement can be in flight. A
    /// folder still held open keeps its mark and is tried again at the next start; a recorded one is never touched, mark
    /// or not.
    /// </remarks>
    public void RemoveLeftBehind(IEnumerable<string> recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);

        if (!Directory.Exists(Root))
        {
            return;
        }

        var kept = recorded.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(folder);

            // Only a name that comes back as itself: Windows drops a trailing dot or space from a path, so deleting a
            // stray "stock." by name would delete the live "stock" beside it.
            if (!kept.Contains(name)
                && string.Equals(Path.GetFileName(FolderPath(name)), name, StringComparison.Ordinal)
                && File.Exists(Path.Combine(folder, LeftBehind)))
            {
                _ = DeleteFolder(name);
            }
        }
    }

    /// <summary>Whether a folder name is taken on disk.</summary>
    /// <param name="folder">The folder name.</param>
    /// <returns>
    /// <see langword="true"/> when a folder has that name in any case, or the name reaches anything in the location.
    /// </returns>
    /// <remarks>
    /// Case-insensitive on every platform, because a location copied from Linux to Windows must not become two
    /// conversations' folders merged into one. **And taken when the path reaches anything at all**: on a volume that
    /// keeps 8.3 names a suffixed name such as <c>ABCDEF~2</c> is another folder's short name, so a conversation given it
    /// would write into, and on a destroy delete, a folder that is somebody else's.
    /// </remarks>
    public bool IsTakenOnDisk(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        return Directory.Exists(Root)
            && (IsItsOwn(folder) || Directory.Exists(FolderPath(folder)) || File.Exists(FolderPath(folder)));
    }

    /// <summary>Whether a folder by this name, in any case, is in the location under its own long name.</summary>
    /// <param name="folder">The folder name.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    /// <remarks>
    /// A path can reach a folder by another's 8.3 short name, so whether it exists is not whether it is this folder.
    /// </remarks>
    private bool IsItsOwn(string folder) =>
        Directory.EnumerateDirectories(Root)
            .Any(d => string.Equals(Path.GetFileName(d), folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>The absolute path of a conversation's folder.</summary>
    /// <param name="folder">The folder name, as the store recorded it.</param>
    /// <returns>The path.</returns>
    public string FolderPath(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        return Contained(Path.Combine(Root, folder));
    }

    /// <summary>The absolute path of a file.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <returns>The path.</returns>
    public string FilePath(string folder, SharedFileName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Contained(Path.Combine(FolderPath(folder), name.Value));
    }

    /// <summary>The name of a file already on disk in a folder under a name with the same key, if there is one.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The name being placed.</param>
    /// <returns>The file's name as it is on disk, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Compared by key, because Windows and macOS hold <c>Notes.md</c> and <c>notes.md</c> as one file, and a placement
    /// committed under either spelling would replace it there. Asked of the disk rather than of the store: a file a
    /// person put in the folder has no row until the watcher or the next list records it, and until then the disk is
    /// the only thing that knows it is there.
    /// </remarks>
    public string? OnDisk(string folder, SharedFileName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var directory = FolderPath(folder);

        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(n => n is not null && string.Equals(SharedFileName.KeyOf(n), name.Key, StringComparison.Ordinal))
                : null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Starts a placement: a staging file that hashes what is written to it.</summary>
    /// <returns>The staged file, which deletes itself unless it is committed.</returns>
    /// <exception cref="IOException">The staging file could not be created, including for want of space.</exception>
    public StagedFile Stage()
    {
        Directory.CreateDirectory(Incoming);

        var path = Path.Combine(Incoming, Guid.NewGuid().ToString("N") + ".part");

        return new StagedFile(this, path, _createStaging(path));
    }

    /// <summary>Holds a file for reading while a download streams it, after any hold over everything has ended.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="cancellationToken">Cancels waiting for a change in progress.</param>
    /// <returns>The hold, carrying the open stream; disposing it ends the hold.</returns>
    /// <exception cref="FileNotFoundException">The file is not on disk.</exception>
    public async Task<ReadHold> HoldForReadingAsync(string folder, SharedFileName name, CancellationToken cancellationToken)
    {
        var path = FilePath(folder, name);
        Gate gate;

        while (true)
        {
            Task? closed;

            // In one lock with the gate, so a hold over everything either sees this gate and waits for its reader, or
            // this read sees the hold and waits for it.
            lock (_gates)
            {
                closed = _everything?.Task;
                gate = GateFor(path);
            }

            if (closed is null)
            {
                break;
            }

            await closed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await gate.EnterReadAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, FileLimits.ChunkBytes, useAsync: true);

            return new ReadHold(stream, () => gate.ExitRead());
        }
        catch
        {
            gate.ExitRead();
            throw;
        }
    }

    /// <summary>Whether any download of a file is in progress.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <returns><see langword="true"/> while a hold is open.</returns>
    public bool IsHeld(string folder, SharedFileName name) => GateFor(FilePath(folder, name)).Readers > 0;

    /// <summary>Waits for every download of a file to finish and holds new ones back until the change is made.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The change's turn; disposing it lets reads in again.</returns>
    /// <remarks>
    /// It does not wait out a hold over everything: that hold enters every file's gate there is, so a change to a file is
    /// in turn with it already, and one to a file never changed or read lands either before the destroy's, purge's or
    /// prune's transaction, and goes with it, or after it, refused or placed again as a new file. Waiting would hold every
    /// agent's replacement behind one hold.
    /// </remarks>
    public Task<IDisposable> ChangeAsync(string folder, SharedFileName name, CancellationToken cancellationToken) =>
        GateFor(FilePath(folder, name)).EnterChangeAsync(cancellationToken);

    /// <summary>The turn of a conversation's first placement, before it has a folder.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The turn; disposing it lets the next in.</returns>
    /// <remarks>
    /// A first placement had nothing to take a turn on, so one could commit between a destroy reading the conversation's
    /// folder (none yet) and destroying it, and its folder stayed on disk for good, unmarked. Its turn is one a hold over
    /// everything waits for, or waits out.
    /// </remarks>
    public Task<IDisposable> FirstPlacementAsync(string conversation, CancellationToken cancellationToken) =>
        EnterAfterAnyHoldAsync("first placement in " + conversation, cancellationToken);

    private async Task<IDisposable> EnterAfterAnyHoldAsync(string key, CancellationToken cancellationToken)
    {
        Gate gate;

        while (true)
        {
            Task? closed;

            // As a read does: a gate made after a hold over everything took its snapshot waits for that hold to end.
            lock (_gates)
            {
                closed = _everything?.Task;
                gate = GateFor(key);
            }

            if (closed is null)
            {
                break;
            }

            await closed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await gate.EnterChangeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for every download anywhere in the location to finish, and holds new ones back, a file's first included, as
    /// it does a conversation's first placement; waits first for another hold over everything to end.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The prune's turn; disposing it lets reads in again.</returns>
    public async Task<IDisposable> ChangeEverythingAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource mine;
        Gate[] gates;

        // One hold over everything at a time: a second waits for the first to end.
        while (true)
        {
            Task? other;

            lock (_gates)
            {
                other = _everything?.Task;

                if (other is null)
                {
                    mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _everything = mine;
                    gates = [.. _gates.Values];

                    break;
                }
            }

            await other.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var turns = new List<IDisposable>();

        try
        {
            foreach (var gate in gates)
            {
                turns.Add(await gate.EnterChangeAsync(cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            turns.ForEach(t => t.Dispose());
            Reopen(mine);
            throw;
        }

        turns.Add(new Reopening(() => Reopen(mine)));

        return new Turns(turns);
    }

    private void Reopen(TaskCompletionSource hold)
    {
        lock (_gates)
        {
            if (ReferenceEquals(_everything, hold))
            {
                _everything = null;
            }
        }

        hold.TrySetResult();
    }

    /// <summary>Deletes one file from the location.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <remarks>Call inside <see cref="ChangeAsync"/>. A file already gone is not an error.</remarks>
    public void Delete(string folder, SharedFileName name) => TryDelete(FilePath(folder, name));

    /// <summary>Deletes a conversation's folder and everything in it.</summary>
    /// <param name="folder">The folder name.</param>
    /// <returns><see langword="true"/> when the folder is gone.</returns>
    public bool DeleteFolder(string folder)
    {
        var path = FolderPath(folder);

        // Only the folder itself, never one its name reaches as a short name: that one is somebody else's.
        if (!Directory.Exists(Root) || !IsItsOwn(folder))
        {
            return true;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(path);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_deleteOnDispose)
        {
            return;
        }

        foreach (var directory in new[] { Root, Incoming })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                // A temporary location a test left something open in; the temp directory is cleaned eventually.
            }
        }
    }

    internal void Commit(string stagedPath, string folder, SharedFileName name, (long SizeBytes, long ModifiedTicks)? expected)
    {
        var target = FilePath(folder, name);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // **Into the folder itself, or nowhere.** A folder can be recorded before it is made (a placement refused for its
        // length still records it), and on a volume that keeps 8.3 names a later folder can take its name as a short
        // name: writing then put one agent's file in another conversation's folder, listed there as the person's.
        if (!IsItsOwn(folder))
        {
            throw new NotItsOwnFolderException(folder);
        }

        // **A replacement waits a moment for a read of the file to finish.** On Windows a move over a file open for reading
        // is refused, and the broker's own watcher reads a file to hash it after every change it sees, so an agent
        // replacing its own file just after placing it meets that read. One hash is short, so a wait of a second outlasts
        // it; a file held longer, by anything, is refused in words.
        //
        // **Only over the file the store recorded.** The placement decided it may replace the file recorded under the name,
        // or that there is none; a person can move that file aside and save their own under the name before the move or
        // while the wait runs, and the move would write the agent's bytes over the person's. So each try first compares
        // what is on disk with the record, and anything else there is refused, with nothing written.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!IsTheRecordedFile(target, expected))
                {
                    throw new Grpc.Core.RpcException(new Grpc.Core.Status(
                        Grpc.Core.StatusCode.Aborted,
                        "The file on disk is not the one Parley recorded under this name, so it was not replaced and "
                        + "nothing was written. Look at the file again before placing it."));
                }

                File.Move(stagedPath, target, overwrite: true);
                return;
            }
            catch (Exception held) when (held is UnauthorizedAccessException or IOException && attempt < 40 && File.Exists(stagedPath))
            {
                Thread.Sleep(25);
            }
            catch (Exception held) when (held is UnauthorizedAccessException or IOException && File.Exists(stagedPath))
            {
                throw new Grpc.Core.RpcException(new Grpc.Core.Status(
                    Grpc.Core.StatusCode.Aborted,
                    "The file was being read, so it was not replaced and nothing was written. Place it again."),
                    held.Message);
            }
        }
    }

    // Whether what is under the name is the file the store recorded: none, for a name nothing holds, or one of the
    // recorded size and write time. A row whose time was never recorded is compared by size alone.
    /// <summary>Whether a file on disk, if there is one, is the one the store recorded under its name.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="recorded">The size and write time recorded.</param>
    /// <returns><see langword="true"/> when no file is there, or the one there has the recorded size and time.</returns>
    /// <remarks>
    /// For a removal by the agent that placed it: a file there that is not the recorded one may be the person's, put under
    /// the name since the agent's went, and the watcher has not yet said so. Removing it would delete the person's bytes.
    /// </remarks>
    internal bool HoldsTheRecordedFileOrNone(string folder, SharedFileName name, (long SizeBytes, long ModifiedTicks) recorded)
    {
        var path = FilePath(folder, name);

        return !File.Exists(path) || IsTheRecordedFile(path, recorded);
    }

    private static bool IsTheRecordedFile(string path, (long SizeBytes, long ModifiedTicks)? expected)
    {
        var info = new FileInfo(path);

        if (expected is not { } recorded)
        {
            return !info.Exists;
        }

        return info.Exists
               && info.Length == recorded.SizeBytes
               && (recorded.ModifiedTicks == 0 || info.LastWriteTimeUtc.Ticks == recorded.ModifiedTicks);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Left for the next start, or already gone.
        }
    }

    private string Contained(string path)
    {
        var full = Path.GetFullPath(path);

        // A name is one flat component by the time it reaches here, and a folder is one the store recorded; this
        // is the second lock on the same door, because a path that escaped the location would write anywhere.
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new InvalidOperationException($"'{path}' is outside the file location.");
    }

    private Gate GateFor(string path)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(path, out var gate))
            {
                gate = new Gate();
                _gates[path] = gate;
            }

            return gate;
        }
    }

    /// <summary>A file being written by a placement, and the hash of what has been written so far.</summary>
    public sealed class StagedFile : IAsyncDisposable
    {
        private readonly FileLocationStore _location;
        private readonly Stream _stream;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _closed;
        private bool _committed;

        internal StagedFile(FileLocationStore location, string path, Stream stream)
        {
            _location = location;
            Path = path;
            _stream = stream;
        }

        /// <summary>Where it is being written.</summary>
        public string Path { get; }

        /// <summary>How many bytes have been written.</summary>
        public long SizeBytes { get; private set; }

        /// <summary>Appends a piece.</summary>
        /// <param name="chunk">The bytes.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task that completes when the piece is written.</returns>
        /// <exception cref="IOException">The write failed, including for want of space.</exception>
        public async Task WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            _hash.AppendData(chunk.Span);
            SizeBytes += chunk.Length;
        }

        /// <summary>Finishes writing and says what was written.</summary>
        /// <param name="cancellationToken">Cancels the flush.</param>
        /// <returns>The hash of every byte written.</returns>
        public async Task<ContentHash> FinishAsync(CancellationToken cancellationToken)
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            _closed = true;

            return ContentHash.FromDigest(_hash.GetHashAndReset());
        }

        /// <summary>Moves the finished file into its folder, over the file the store recorded under that name if any.</summary>
        /// <param name="folder">The conversation's folder name.</param>
        /// <param name="name">The file's name.</param>
        /// <param name="expected">The size and write time recorded for the file it replaces, or <see langword="null"/> when none is recorded.</param>
        /// <returns>The last-write time the committed file carries.</returns>
        /// <exception cref="Grpc.Core.RpcException">
        /// <c>Aborted</c>: what is on disk under the name is not the recorded file, or it stayed in use; nothing was written.
        /// </exception>
        /// <remarks>Call inside <see cref="FileLocationStore.ChangeAsync"/> when a file of that name may exist.</remarks>
        public long CommitTo(string folder, SharedFileName name, (long SizeBytes, long ModifiedTicks)? expected)
        {
            if (!_closed)
            {
                throw new InvalidOperationException("A staged file is committed only once it is finished.");
            }

            _location.Commit(Path, folder, name, expected);
            _committed = true;

            return File.GetLastWriteTimeUtc(_location.FilePath(folder, name)).Ticks;
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _closed = true;
            }

            _hash.Dispose();

            if (!_committed)
            {
                TryDelete(Path);
            }
        }
    }

    /// <summary>A download's hold on a file, and the stream it reads.</summary>
    public sealed class ReadHold : IAsyncDisposable
    {
        private readonly Action _release;
        private int _released;

        internal ReadHold(Stream stream, Action release)
        {
            Stream = stream;
            _release = release;
        }

        /// <summary>The file, open for reading with no write or delete sharing.</summary>
        public Stream Stream { get; }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync().ConfigureAwait(false);

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _release();
            }
        }
    }

    private sealed class Turns(List<IDisposable> turns) : IDisposable
    {
        public void Dispose() => turns.ForEach(t => t.Dispose());
    }

    /// <summary>A conversation's folder that the disk resolves to another folder, by its short name.</summary>
    /// <param name="folder">The folder the store recorded.</param>
    internal sealed class NotItsOwnFolderException(string folder)
        : IOException($"The folder '{folder}' is another folder's short name on this volume.");

    private sealed class Reopening(Action reopen) : IDisposable
    {
        public void Dispose() => reopen();
    }

    /// <summary>Readers and one change at a time for one file, with a waiting change holding new readers back.</summary>
    private sealed class Gate
    {
        private readonly object _lock = new();
        private int _readers;
        private bool _changing;
        private TaskCompletionSource _drained = Completed();
        private TaskCompletionSource _changed = Completed();

        public int Readers
        {
            get
            {
                lock (_lock)
                {
                    return _readers;
                }
            }
        }

        public async Task EnterReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;

                lock (_lock)
                {
                    if (!_changing)
                    {
                        _readers++;
                        return;
                    }

                    wait = _changed.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void ExitRead()
        {
            lock (_lock)
            {
                _readers--;

                if (_readers == 0)
                {
                    _drained.TrySetResult();
                }
            }
        }

        public async Task<IDisposable> EnterChangeAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;

                lock (_lock)
                {
                    if (!_changing)
                    {
                        _changing = true;
                        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _drained = _readers == 0
                            ? Completed()
                            : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        break;
                    }

                    wait = _changed.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                Task drained;

                lock (_lock)
                {
                    drained = _drained.Task;
                }

                await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ExitChange();
                throw;
            }

            return new Release(ExitChange);
        }

        private void ExitChange()
        {
            lock (_lock)
            {
                _changing = false;
                _changed.TrySetResult();
            }
        }

        private static TaskCompletionSource Completed()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done.SetResult();
            return done;
        }

        private sealed class Release(Action release) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    release();
                }
            }
        }
    }
}
