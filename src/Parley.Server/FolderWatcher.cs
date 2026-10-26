using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Parley.Core;
using Parley.Store;

namespace Parley.Server;

/// <summary>
/// Notices changes made to the file location outside Parley, and brings the store into line with them (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>A person may edit, rename or delete a file in its folder, and that is the file's new state.</b>
/// An edit gets a new hash and every agent that fetched the file is told; a rename keeps the file's placer; a deletion is
/// the file gone; a file copied in by hand is the person's. None of it is refused.
/// </para>
/// <para>
/// <b>Change is decided by hash, never by event.</b> An editor writes several times per save, a placement moves its own
/// bytes into place, and Windows drops events when its buffer overflows. So every event only schedules a look at one
/// path, half a second later, and the look hashes what is there and compares it with the row — any number of events
/// for one save come to at most one change and one set of notices, and a placement's own move comes to none.
/// </para>
/// <para>
/// <b>Events are a way to be prompt, not the guarantee.</b> A missed change to a recorded file is caught by the reconcile
/// before every list and fetch, a look that could not read the file is made again, and a change made while the broker
/// was stopped is caught by the scan of the whole location at start.
/// </para>
/// <para>
/// <b>A disappearance is judged when the half second is up, not when it is reported.</b> An editor saving by rename moves
/// the original away and a new file into its place; judged at the first event, that would be the file gone and a new
/// one appearing as the person's, and the placer would be lost. Judged after, the file is there, and it has changed.
/// </para>
/// <para>
/// <b>A rename is recognised from the operating system's report of one, and the report outlives the events after
/// it.</b> A move raises a rename and, depending on the platform, creations and changes at the new path and a deletion
/// at the old one — each of which replaces the pending look at its path. So the report is kept apart from the pending looks, and whichever look
/// runs first, at the name the file left or the name it took, carries the placer across; the other finds it done.
/// A file that goes and one with the same content that appears, with no rename reported, are a deletion and the
/// person's new file: matching by hash is the scan at start's rule alone, because only there is no report to be had.
/// </para>
/// </remarks>
internal sealed class FolderWatcher : IAsyncDisposable
{
    private readonly BrokerRuntime _runtime;
    private readonly TimeSpan _settle;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _renamedFrom = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _renamedTo = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private FileSystemWatcher? _watcher;
    private int _disposed;

    /// <summary>Creates a watcher for a runtime's file location.</summary>
    /// <param name="runtime">The runtime whose location and store this reconciles.</param>
    /// <param name="settle">How long a path is left quiet before it is looked at.</param>
    public FolderWatcher(BrokerRuntime runtime, TimeSpan? settle = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _settle = settle ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Completes when the scan of the whole location, started with the watcher, has finished.</summary>
    public Task Scanned { get; private set; } = Task.CompletedTask;

    /// <summary>Starts watching, and scans the whole location once for what changed while nothing was.</summary>
    public void Start()
    {
        Directory.CreateDirectory(_runtime.Files.Root);

        var watcher = new FileSystemWatcher(_runtime.Files.Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        watcher.Created += (_, e) => Schedule(e.FullPath);
        watcher.Changed += (_, e) => Schedule(e.FullPath);
        watcher.Deleted += (_, e) => Schedule(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            _renamedFrom[e.FullPath] = e.OldFullPath;
            _renamedTo[e.OldFullPath] = e.FullPath;

            Schedule(e.OldFullPath);
            Schedule(e.FullPath);
        };

        // An overflowed buffer lost events without saying which. The whole location is looked at instead.
        watcher.Error += (_, _) => Scanned = Guarded(ScanAsync);

        watcher.EnableRaisingEvents = true;
        _watcher = watcher;

        Scanned = Guarded(ScanAsync);
    }

    /// <summary>Brings the store into line with the whole location on disk.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>A task that completes when every recorded folder has been looked at.</returns>
    /// <remarks>
    /// A file with no row whose hash matches exactly one row whose file went missing from the same folder is that file
    /// renamed, and keeps its placer. Any other file with no row is the person's, and any row whose
    /// file is still missing is gone.
    /// </remarks>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        var (folders, rows) = await _runtime.UseConnectionAsync(
            async (connection, _, ct) =>
            {
                var files = new SharedFileRepository(connection);

                return (
                    await files.GetFoldersAsync(cancellationToken: ct).ConfigureAwait(false),
                    await files.GetAllAsync(cancellationToken: ct).ConfigureAwait(false));
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var (conversation, folder) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = _runtime.Files.FolderPath(folder);
            var onDisk = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Select(Path.GetFileName)
                    .Where(name => SharedFileName.Check(name) == SharedFileNameProblem.None)
                    .Select(name => SharedFileName.Create(name))
                    .ToDictionary(name => name.Key, StringComparer.Ordinal)
                : [];

            var recorded = rows.Where(r => r.File.ConversationId == conversation).ToList();
            var missing = recorded.Where(r => !onDisk.ContainsKey(r.NameKey)).ToList();

            foreach (var row in recorded.Where(r => onDisk.ContainsKey(r.NameKey)))
            {
                await ReconcileAsync(row, cancellationToken).ConfigureAwait(false);
            }

            foreach (var name in onDisk.Values.Where(n => recorded.All(r => r.NameKey != n.Key)))
            {
                var path = _runtime.Files.FilePath(folder, name);

                if (await MeasureAsync(path, cancellationToken).ConfigureAwait(false) is not { } measured)
                {
                    continue;
                }

                var matches = missing.Where(r => r.File.Sha256 == measured.Sha256).ToList();

                await _runtime.UseConnectionAsync(
                    async (connection, now, ct) =>
                    {
                        var files = new SharedFileRepository(connection);

                        if (matches.Count == 1)
                        {
                            _ = await files.RenameAsync(matches[0], name, measured.ModifiedTicks, now, cancellationToken: ct)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            _ = await files.RecordFoundAsync(
                                conversation, name, measured.Sha256, measured.SizeBytes, measured.ModifiedTicks, now,
                                cancellationToken: ct).ConfigureAwait(false);
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);

                if (matches.Count == 1)
                {
                    missing.Remove(matches[0]);
                }
            }

            foreach (var gone in missing)
            {
                await _runtime.UseConnectionAsync(
                    (connection, now, ct) => new SharedFileRepository(connection).RemoveAsync(gone, null, now, cancellationToken: ct),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Once: a runtime disposed by its server and again by whoever opened it must not cancel a disposed source.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        foreach (var pending in _pending.Values)
        {
            pending.Dispose();
        }

        try
        {
            await Scanned.ConfigureAwait(false);
        }
        catch (Exception finished) when (finished is OperationCanceledException or ObjectDisposedException)
        {
            // Stopping is how it ends.
        }

        _stopping.Dispose();
    }

    private void Schedule(string path) => Schedule(path, attempt: 0);

    /// <summary>Looks at a path once it has been quiet, and again later if the look could not be made.</summary>
    /// <remarks>
    /// <b>A look that fails is made again.</b> A file just written is often held for a moment by whatever else watches
    /// the disk, a virus scanner on Windows most of all, and a list reconciles only the files already recorded. So a
    /// new file whose one look met it held was not listed until something else changed or the broker restarted. Each
    /// later look waits twice as long, up to <see cref="LongestRetry"/>, and an event at the path replaces it.
    /// </remarks>
    private void Schedule(string path, int attempt)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        var wait = attempt == 0
            ? _settle
            : TimeSpan.FromTicks(Math.Min(_settle.Ticks << Math.Min(attempt, 16), LongestRetry.Ticks));

        var quiet = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);

        _pending.AddOrUpdate(
            path,
            quiet,
            (_, previous) =>
            {
                previous.Cancel();
                return quiet;
            });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(wait, quiet.Token).ConfigureAwait(false);

                // Only the latest look at a path goes ahead; an earlier one was cancelled above.
                _ = ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_pending)
                    .Remove(new KeyValuePair<string, CancellationTokenSource>(path, quiet));

                await LookAtAsync(path, quiet.Token).ConfigureAwait(false);
            }
            catch (Exception ended) when (ended is OperationCanceledException or ObjectDisposedException)
            {
                // Superseded by a later event, or the broker is stopping.
            }
            catch (Exception unexpected) when (unexpected is IOException or UnauthorizedAccessException
                                                   or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // A file held by an editor or a scanner, or a row that moved under the look: looked at again, unless an
                // event at the path has already asked for a look of its own.
                if (!_pending.ContainsKey(path))
                {
                    Schedule(path, attempt + 1);
                }
            }
            finally
            {
                quiet.Dispose();
            }
        });
    }

    private async Task LookAtAsync(string path, CancellationToken cancellationToken)
    {
        // Taken when the look runs rather than when the event arrived: a look superseded before it ran leaves the report
        // for the look that replaced it.
        _renamedFrom.TryRemove(path, out var renamedFrom);
        _renamedTo.TryRemove(path, out var renamedTo);

        // A folder that has just appeared. On Linux a new folder is watched only once its own creation has been
        // handled, so whatever was written into it before then raised no event at all. Each file in it, and each the
        // store records there, is looked at as an event would have asked: the same looks, by the same rules, so a
        // rename is still recognised only from a report of one and never by matching hashes.
        if (Directory.Exists(path))
        {
            var here = Path.GetFileName(path);
            var recorded = await _runtime.UseConnectionAsync(
                (connection, _, ct) => new SharedFileRepository(connection).GetAllAsync(cancellationToken: ct),
                cancellationToken).ConfigureAwait(false);

            foreach (var file in Directory.EnumerateFiles(path)
                         .Concat(recorded
                             .Where(r => string.Equals(r.File.Folder, here, StringComparison.OrdinalIgnoreCase))
                             .Select(r => _runtime.Files.FilePath(r.File.Folder, r.File.Name)))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Schedule(file);
            }

            return;
        }

        if (!Split(path, out var folder, out var fileName))
        {
            return;
        }

        var conversation = await _runtime.UseConnectionAsync(
            (connection, _, ct) => new SharedFileRepository(connection).GetConversationForFolderAsync(folder, cancellationToken: ct),
            cancellationToken).ConfigureAwait(false);

        if (conversation is null)
        {
            return;
        }

        SharedFileName.TryCreate(fileName, out var name);

        var row = name is null
            ? null
            : await _runtime.UseConnectionAsync(
                (connection, _, ct) => new SharedFileRepository(connection).GetAsync(conversation, name, cancellationToken: ct),
                cancellationToken).ConfigureAwait(false);

        if (name is null || !File.Exists(path))
        {
            // Gone, or renamed to a name that is not in the location. Judged now, after the quiet period: an editor that
            // moved the file away to save it has put it back by now, and the look at its path finds it changed.
            if (row is not null && !File.Exists(path))
            {
                // Moved to a name the location holds: the placer goes with it, whether or not the look at that name
                // has run.
                if (renamedTo is not null && File.Exists(renamedTo) && Within(renamedTo, folder, out var movedTo))
                {
                    var ticks = File.GetLastWriteTimeUtc(renamedTo).Ticks;

                    await _runtime.UseConnectionAsync(
                        (connection, now, ct) => new SharedFileRepository(connection).RenameAsync(row, movedTo, ticks, now, cancellationToken: ct),
                        cancellationToken).ConfigureAwait(false);

                    return;
                }

                await _runtime.UseConnectionAsync(
                    (connection, now, ct) => new SharedFileRepository(connection).RemoveAsync(row, null, now, cancellationToken: ct),
                    cancellationToken).ConfigureAwait(false);
            }

            // Renamed away to a name the location does not accept, and not put back: the name it had is gone.
            if (renamedFrom is not null && !File.Exists(renamedFrom) && Within(renamedFrom, folder, out var awayFrom))
            {
                await _runtime.UseConnectionAsync(
                    async (connection, now, ct) =>
                    {
                        var files = new SharedFileRepository(connection);

                        return await files.GetAsync(conversation, awayFrom, cancellationToken: ct).ConfigureAwait(false) is { } left
                            ? await files.RemoveAsync(left, null, now, cancellationToken: ct).ConfigureAwait(false)
                            : FileRejection.NoSuchFile;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (row is not null)
        {
            await ReconcileAsync(row, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await MeasureAsync(path, cancellationToken).ConfigureAwait(false) is not { } measured)
        {
            return;
        }

        await _runtime.UseConnectionAsync(
            async (connection, now, ct) =>
            {
                var files = new SharedFileRepository(connection);

                // A rename keeps its placer, but only when the file it came from is really gone: a copy leaves the
                // original where it was, and a copy is a new file, the person's.
                if (renamedFrom is not null
                    && !File.Exists(renamedFrom)
                    && Within(renamedFrom, folder, out var oldName)
                    && await files.GetAsync(conversation, oldName, cancellationToken: ct).ConfigureAwait(false) is { } renamed)
                {
                    var kept = await files.RenameAsync(renamed, name, measured.ModifiedTicks, now, cancellationToken: ct)
                        .ConfigureAwait(false);

                    if (kept is not null)
                    {
                        _ = await files.ReconcileAsync(
                            kept, measured.Sha256, measured.SizeBytes, measured.ModifiedTicks, now, cancellationToken: ct)
                            .ConfigureAwait(false);
                    }

                    return true;
                }

                _ = await files.RecordFoundAsync(
                    conversation, name, measured.Sha256, measured.SizeBytes, measured.ModifiedTicks, now,
                    cancellationToken: ct).ConfigureAwait(false);

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(StoredFile row, CancellationToken cancellationToken)
    {
        var path = _runtime.Files.FilePath(row.File.Folder, row.File.Name);
        var info = new FileInfo(path);

        if (!info.Exists || (info.Length == row.File.SizeBytes && info.LastWriteTimeUtc.Ticks == row.ModifiedTicks))
        {
            return;
        }

        if (await MeasureAsync(path, cancellationToken).ConfigureAwait(false) is not { } measured)
        {
            return;
        }

        await _runtime.UseConnectionAsync(
            (connection, now, ct) => new SharedFileRepository(connection).ReconcileAsync(
                row, measured.Sha256, measured.SizeBytes, measured.ModifiedTicks, now, cancellationToken: ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hashes a file, or says it is gone; a file that is there and cannot be read is an error.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the hash.</param>
    /// <returns>What it holds, or <see langword="null"/> when there is no file.</returns>
    /// <exception cref="IOException">The file is there and could not be read.</exception>
    /// <remarks>
    /// <see cref="FileLocationStore.HashAsync"/> answers null for both, and a look that took a held file for a gone one
    /// returned as though there were nothing to do. Thrown, it is looked at again (<c>Schedule</c>, <c>Guarded</c>).
    /// </remarks>
    private static async Task<(ContentHash Sha256, long SizeBytes, long ModifiedTicks)?> MeasureAsync(
        string path, CancellationToken cancellationToken)
    {
        var measured = await FileLocationStore.HashAsync(path, cancellationToken).ConfigureAwait(false);

        return measured is null && File.Exists(path)
            ? throw new IOException($"'{path}' is there and could not be read, held by another program.")
            : measured;
    }

    /// <summary>Whether a path is a file in a given conversation folder under a name the location accepts.</summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder it must be in.</param>
    /// <param name="name">The name, when it is.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    private bool Within(string path, string folder, [NotNullWhen(true)] out SharedFileName? name)
    {
        name = null;

        return Split(path, out var other, out var fileName)
               && string.Equals(other, folder, StringComparison.OrdinalIgnoreCase)
               && SharedFileName.TryCreate(fileName, out name);
    }

    /// <summary>Splits a path into the conversation folder and file name it would be, if it is exactly that deep.</summary>
    private bool Split(string path, out string folder, out string fileName)
    {
        folder = string.Empty;
        fileName = string.Empty;

        var relative = Path.GetRelativePath(_runtime.Files.Root, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 || parts[0] == "..")
        {
            return false;
        }

        folder = parts[0];
        fileName = parts[1];
        return true;
    }

    /// <summary>Runs a scan of the whole location, and runs it again later if a file or the store was busy.</summary>
    /// <remarks>
    /// One held file ended the scan at the first folder it met, and what the scan was for (a change made while the
    /// broker was stopped, a file put there by hand) waited for the next start. Each later attempt waits twice as long,
    /// up to <see cref="LongestRetry"/>.
    /// </remarks>
    private Task Guarded(Func<CancellationToken, Task> work) =>
        Task.Run(async () =>
        {
            for (var wait = _settle; ; wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestRetry.Ticks)))
            {
                try
                {
                    await work(_stopping.Token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ended) when (ended is OperationCanceledException or ObjectDisposedException)
                {
                    // Stopping.
                    return;
                }
                catch (Exception busy) when (busy is IOException or UnauthorizedAccessException
                                                 or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
                {
                    // A file held by somebody else, or the store busy: tried again.
                }

                try
                {
                    await Task.Delay(wait, _stopping.Token).ConfigureAwait(false);
                }
                catch (Exception ended) when (ended is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
            }
        });

    /// <summary>The longest a failed look or scan waits before it is tried again.</summary>
    private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(30);
}
