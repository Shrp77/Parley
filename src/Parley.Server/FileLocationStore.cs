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
/// <b>A placement is staged beside the location and moved in when complete</b>, which is the owner's call: the
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

    /// <summary>Whether a folder name is taken on disk, compared without regard to case.</summary>
    /// <param name="folder">The folder name.</param>
    /// <returns><see langword="true"/> when a directory by that name, in any case, is already in the location.</returns>
    /// <remarks>
    /// Case-insensitive on every platform, because a location copied from Linux to Windows must not become two
    /// conversations' folders merged into one.
    /// </remarks>
    public bool IsTakenOnDisk(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        return Directory.Exists(Root)
            && Directory.EnumerateDirectories(Root)
                .Any(d => string.Equals(Path.GetFileName(d), folder, StringComparison.OrdinalIgnoreCase));
    }

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

    /// <summary>Holds a file for reading while a download streams it.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="cancellationToken">Cancels waiting for a change in progress.</param>
    /// <returns>The hold, carrying the open stream; disposing it ends the hold.</returns>
    /// <exception cref="FileNotFoundException">The file is not on disk.</exception>
    public async Task<ReadHold> HoldForReadingAsync(string folder, SharedFileName name, CancellationToken cancellationToken)
    {
        var path = FilePath(folder, name);
        var gate = GateFor(path);

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
    public Task<IDisposable> ChangeAsync(string folder, SharedFileName name, CancellationToken cancellationToken) =>
        GateFor(FilePath(folder, name)).EnterChangeAsync(cancellationToken);

    /// <summary>Waits for every download anywhere in the location to finish, and holds new ones back.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The prune's turn; disposing it lets reads in again.</returns>
    public async Task<IDisposable> ChangeEverythingAsync(CancellationToken cancellationToken)
    {
        Gate[] gates;

        lock (_gates)
        {
            gates = [.. _gates.Values];
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
            throw;
        }

        return new Turns(turns);
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

    internal void Commit(string stagedPath, string folder, SharedFileName name)
    {
        var target = FilePath(folder, name);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(stagedPath, target, overwrite: true);
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

        /// <summary>Moves the finished file into its folder, replacing what is there.</summary>
        /// <param name="folder">The conversation's folder name.</param>
        /// <param name="name">The file's name.</param>
        /// <returns>The last-write time the committed file carries.</returns>
        /// <remarks>Call inside <see cref="FileLocationStore.ChangeAsync"/> when a file of that name may exist.</remarks>
        public long CommitTo(string folder, SharedFileName name)
        {
            if (!_closed)
            {
                throw new InvalidOperationException("A staged file is committed only once it is finished.");
            }

            _location.Commit(Path, folder, name);
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
