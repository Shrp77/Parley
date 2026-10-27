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
/// <b>A disappearance is judged after the half second, and only on a second look.</b> An editor saving by rename moves
/// the original away and a new file into its place; judged at the first event, that would be the file gone and a new
/// one appearing as the person's, and the placer would be lost. Judged after, the file is there, and it has changed. A
/// file still gone with no report of where it went is looked at once more a half second later, by when any rename has
/// reported itself, and only then is it gone.
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
    // **Looks and rename reports are keyed by exact spelling.** Keyed without case, a case twin's events merged with the
    // recorded file's: one look took the twin's rename as the agent's, or a twin's report overwrote the agent's own, and
    // a placer was carried onto the person's file or a row lost with its file on disk. A folder that does not tell case
    // apart reports one spelling for a file, and a look at another spelling of a recorded name is made at the recorded
    // one.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _renamedFrom = new(StringComparer.Ordinal);
    // Each report keeps the spelling it moved from, which a twin's rename is told apart by.
    private readonly ConcurrentDictionary<string, (string From, string To)> _renamedTo = new(StringComparer.Ordinal);
    // Paths a look found gone with no report of where they went, judged by the next look at them.
    private readonly ConcurrentDictionary<string, byte> _goneOnce = new(StringComparer.Ordinal);
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
            // **A rename of a rename not yet looked at continues it.** a.md to b.md and on to c.md within the quiet
            // period left a's look a report naming b.md, gone by then, so the row went and c.md was found as new. The
            // first name's report is moved on to the last, only while its look has not taken it.
            var from = e.OldFullPath;

            if (_renamedFrom.TryGetValue(e.OldFullPath, out var origin) && _renamedTo.TryUpdate(origin, (origin, e.FullPath), (origin, e.OldFullPath)))
            {
                // The middle name's report goes with it, or that name's look, run before the first, would take the first
                // name as renamed away and remove its row.
                _ = ((ICollection<KeyValuePair<string, string>>)_renamedFrom).Remove(new KeyValuePair<string, string>(e.OldFullPath, origin));
                from = origin;
            }

            _renamedFrom[e.FullPath] = from;
            _renamedTo[e.OldFullPath] = (e.OldFullPath, e.FullPath);

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
    /// <exception cref="IOException">
    /// A file was held by another program and passed over; every other file was looked at, and the scan should run
    /// again.
    /// </exception>
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

        // **A held file is passed over, never the scan.** Thrown at the first, it left every folder after it unscanned
        // for as long as the file stayed held. Each is skipped and the pass finishes; the scan then runs again for them.
        var held = false;

        foreach (var (conversation, folder) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var heldHere = false;

            var directory = _runtime.Files.FolderPath(folder);
            var recorded = rows.Where(r => r.File.ConversationId == conversation).ToList();
            List<SharedFileName> names;

            try
            {
                names = Directory.Exists(directory)
                    ? [.. Directory.EnumerateFiles(directory)
                        .Select(Path.GetFileName)
                        .Where(name => SharedFileName.Check(name) == SharedFileNameProblem.None)
                        .Select(name => SharedFileName.Create(name))]
                    : [];
            }
            catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed is passed over whole, its rows judged on the next pass.
                held = true;
                continue;
            }

            // **One file to a name, as the location has.** Names are compared without case, and a folder that tells
            // case apart (every folder on Linux) can hold two that differ only in it. The recorded one is the file; with
            // none recorded, the first in ordinal order. The other is not in the location, as a name breaking a rule is.
            var onDisk = names
                .GroupBy(name => name.Key, StringComparer.Ordinal)
                .ToDictionary(
                    same => same.Key,
                    same => same.FirstOrDefault(n => recorded.Exists(r => r.File.Name.Value == n.Value))
                        ?? same.OrderBy(n => n.Value, StringComparer.Ordinal).First(),
                    StringComparer.Ordinal);
            var missing = recorded.Where(r => !onDisk.ContainsKey(r.NameKey)).ToList();

            foreach (var row in recorded.Where(r => onDisk.ContainsKey(r.NameKey)))
            {
                try
                {
                    await ReconcileAsync(row, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
                {
                    held = true;
                }
            }

            foreach (var name in onDisk.Values.Where(n => recorded.All(r => r.NameKey != n.Key)))
            {
                var path = _runtime.Files.FilePath(folder, name);
                (ContentHash Sha256, long SizeBytes, long ModifiedTicks)? read;

                try
                {
                    read = await MeasureAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
                {
                    held = heldHere = true;
                    continue;
                }

                if (read is not { } measured)
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

            // Not while a new file here could not be read: it may be one of these renamed, and judged gone now its placer
            // would be lost. They are judged when the scan runs again.
            foreach (var gone in missing.Where(_ => !heldHere))
            {
                await _runtime.UseConnectionAsync(
                    (connection, now, ct) => new SharedFileRepository(connection).RemoveAsync(gone, null, now, cancellationToken: ct),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (held)
        {
            throw new IOException("A file in the location was held by another program; the scan runs again for it.");
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
#pragma warning disable CA1031 // A scan that failed must not stop the broker from stopping and releasing its store.
        catch (Exception)
        {
            // Stopping is how it ends; any other failure ended it already, and the store must still be released.
        }
#pragma warning restore CA1031

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
        _renamedTo.TryRemove(path, out var report);
        string? renamedTo = report.To;

        // Consumed by whichever look comes next at this path, so a mark never outlives one look.
        var confirming = _goneOnce.TryRemove(path, out _);
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

        // **An event at a case twin is a look at the recorded file.** Rows are found without case, and a folder that tells
        // case apart can hold the recorded file and another differing only in case, which is not in the location (SPEC.md
        // §9.5). So the look is made at the recorded spelling, with the event's rename report: where case is not told
        // apart they are one file and nothing changes. A twin left behind once the recorded file has gone is looked at
        // afterwards on its own, as the scan would.
        if (row is not null && name is not null && !string.Equals(row.File.Name.Value, name.Value, StringComparison.Ordinal))
        {
            var recordedPath = _runtime.Files.FilePath(row.File.Folder, row.File.Name);

            // **The recorded file gone where case is told apart is for the look at its own spelling.** This spelling's
            // rename report is not the recorded file's: that look, with its own report, carries the placer on a rename
            // (a case-only one included) or lets the row go, and then looks at any twin left behind. (Where this spelling
            // is there and the recorded one is not, case is told apart by definition.)
            if (!File.Exists(recordedPath) && (File.Exists(path) || TellsCaseApart(_runtime.Files.FolderPath(row.File.Folder))))
            {
                Schedule(recordedPath);
                return;
            }

            path = recordedPath;
            name = row.File.Name;
        }

        if (name is null || !File.Exists(path))
        {
            // Gone, or renamed to a name that is not in the location. Judged after the quiet period, and gone only on a
            // second look: an editor that moved the file away to save it has put it back by now, and the look at its path
            // finds it changed.
            if (row is not null && !File.Exists(path))
            {
                // Moved to a name the location holds: the placer goes with it, whether or not the look at that name
                // has run.
                if (renamedTo is not null && File.Exists(renamedTo) && Within(renamedTo, folder, out var movedTo)
                    && MovedTheRecordedFile(report.From, row))
                {
                    // **The row moves with the time it was last reconciled at, not the file's time now.** A same-size edit
                    // saved after the rename and before this look stamped the old hash with the new time, so no look or
                    // scan ever hashed the file again and every fetch was refused as changed. The look at the new name,
                    // asked for below, finds the time moved and hashes it.
                    var ticks = row.ModifiedTicks;

                    // A name in another case beside a recorded file still there is that file's twin, not in the location:
                    // moving onto it is moving away to a name that breaks a rule, and the recorded file is not displaced.
                    await _runtime.UseConnectionAsync(
                        async (connection, now, ct) =>
                        {
                            var files = new SharedFileRepository(connection);

                            if (await files.GetAsync(conversation, movedTo, cancellationToken: ct).ConfigureAwait(false) is { } there
                                && BothOnDisk(renamedTo, there.File.Name.Value))
                            {
                                _ = await files.RemoveAsync(row, null, now, cancellationToken: ct).ConfigureAwait(false);
                                return true;
                            }

                            _ = await files.RenameAsync(row, movedTo, ticks, now, cancellationToken: ct).ConfigureAwait(false);
                            return true;
                        },
                        cancellationToken).ConfigureAwait(false);

                    Schedule(renamedTo);
                    LookAtTwins(path);

                    return;
                }

                // **Gone is judged on a second look, a quiet period later.** A look that ran before the rename's event
                // arrived, or that took its report before the rename happened, held no report naming where the file went,
                // so it removed the row and the new name was found as the person's. By the second look any rename has
                // reported itself, and the look carrying that report moves the row instead.
                if (!confirming)
                {
                    _goneOnce[path] = 0;
                    Schedule(path);
                    return;
                }

                await _runtime.UseConnectionAsync(
                    (connection, now, ct) => new SharedFileRepository(connection).RemoveAsync(row, null, now, cancellationToken: ct),
                    cancellationToken).ConfigureAwait(false);

                LookAtTwins(path);
            }
            else if (row is null && name is not null)
            {
                // Gone with no row: an agent removed its own file, and the row went with it before this look. A twin left
                // beside it is a file of its own now, and nothing else would look at it.
                LookAtTwins(path);
            }

            // Renamed away to a name the location does not accept, and not put back: the name it had is gone.
            if (renamedFrom is not null && !File.Exists(renamedFrom) && Within(renamedFrom, folder, out var awayFrom))
            {
                await _runtime.UseConnectionAsync(
                    async (connection, now, ct) =>
                    {
                        var files = new SharedFileRepository(connection);

                        return await files.GetAsync(conversation, awayFrom, cancellationToken: ct).ConfigureAwait(false) is { } left
                               && Gone(left)
                               && MovedTheRecordedFile(renamedFrom, left)
                            ? await files.RemoveAsync(left, null, now, cancellationToken: ct).ConfigureAwait(false)
                            : FileRejection.NoSuchFile;
                    },
                    cancellationToken).ConfigureAwait(false);

                LookAtTwins(renamedFrom);
            }

            return;
        }

        // **A file back where a look found it gone is a new file, the person's** (SPEC.md §9.5: an edit is a file put back
        // within the half second before the first look). The second look waits a quiet period, so without this a file
        // the person put under the name in that time read as an edit of the agent's, which the agent could then remove.
        // An editor saving by rename has its file back long before the first look, which never marks it. A rename
        // reported in the meantime still carries the placer to the new name below.
        if (row is not null && confirming)
        {
            await _runtime.UseConnectionAsync(
                (connection, now, ct) => new SharedFileRepository(connection).RemoveAsync(row, null, now, cancellationToken: ct),
                cancellationToken).ConfigureAwait(false);

            row = null;
        }

        if (row is not null)
        {
            await ReconcileAsync(row, cancellationToken, always: true).ConfigureAwait(false);
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
                    && await files.GetAsync(conversation, oldName, cancellationToken: ct).ConfigureAwait(false) is { } renamed
                    && Gone(renamed)
                    && MovedTheRecordedFile(renamedFrom, renamed))
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

        // A placer carried here from a recorded file may leave a twin of its old name, a file of its own from now on.
        if (renamedFrom is not null)
        {
            LookAtTwins(renamedFrom);
        }
    }

    /// <summary>Looks at a recorded file again, hashing it whatever its size and time say.</summary>
    /// <param name="folder">The file's folder.</param>
    /// <param name="name">The file's name.</param>
    /// <remarks>
    /// For a fetch that streamed bytes other than the recorded hash: without a look, a change that kept the file's size
    /// and time (a coarse file-system clock, <c>rsync -t</c>) was refused as changed on every fetch, for good. And for a
    /// list or fetch whose measurement was dropped because the row moved on meanwhile, perhaps from older bytes.
    /// </remarks>
    internal void LookAgain(string folder, SharedFileName name) => Schedule(_runtime.Files.FilePath(folder, name));

    /// <summary>Forgets that a look found a file gone, because a placement through Parley has just written it.</summary>
    /// <param name="folder">The file's folder.</param>
    /// <param name="name">The file's name.</param>
    /// <remarks>
    /// Without it, the second look found a file back where one went and recorded the agent's own placement as the
    /// person's.
    /// </remarks>
    internal void Placed(string folder, SharedFileName name) => _goneOnce.TryRemove(_runtime.Files.FilePath(folder, name), out _);

    /// <summary>Whether a look found a recorded file gone and the next look has yet to judge it.</summary>
    /// <param name="folder">The file's folder.</param>
    /// <param name="name">The file's name.</param>
    /// <returns><see langword="true"/> while what is under the name now is undecided: an edit, or the person's file.</returns>
    /// <remarks>
    /// A list or fetch that recorded what it found there in that time made the person's new file an edit of the agent's,
    /// which the agent could then remove; only the look that judges it may say which it is.
    /// </remarks>
    internal bool Judging(string folder, SharedFileName name) => _goneOnce.ContainsKey(_runtime.Files.FilePath(folder, name));

    /// <summary>Brings a row into line with its file.</summary>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancels the hash.</param>
    /// <param name="always">Whether to hash even when the size and time match the row.</param>
    /// <remarks>
    /// <b>A look that a change asked for always hashes.</b> Size and time are a shortcut for a scan, which has no event
    /// to go on; a change that keeps both (a coarse clock, a tool restoring the time) was otherwise recorded as no
    /// change, and the old hash listed and every fetch refused.
    /// </remarks>
    private async Task ReconcileAsync(StoredFile row, CancellationToken cancellationToken, bool always = false)
    {
        var path = _runtime.Files.FilePath(row.File.Folder, row.File.Name);
        var info = new FileInfo(path);

        if (!info.Exists || (!always && info.Length == row.File.SizeBytes && info.LastWriteTimeUtc.Ticks == row.ModifiedTicks))
        {
            return;
        }

        if (await MeasureAsync(path, cancellationToken).ConfigureAwait(false) is not { } measured)
        {
            return;
        }

        var outcome = await _runtime.UseConnectionAsync(
            (connection, now, ct) => new SharedFileRepository(connection).ReconcileAsync(
                row, measured.Sha256, measured.SizeBytes, measured.ModifiedTicks, now, cancellationToken: ct),
            cancellationToken).ConfigureAwait(false);

        // **A measurement dropped because the row moved on is measured again.** Two looks at a large file can overlap and
        // the older finish first; its write moved the row on, and this newer measurement, dropped, was the only one of the
        // file as it now is. Looked at again, it is measured against the row as it now stands.
        if (outcome == ReconcileOutcome.MovedOn)
        {
            Schedule(path);
        }
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

    /// <summary>Whether a recorded file is no longer on disk under its recorded spelling.</summary>
    /// <remarks>
    /// A rename report names the spelling that moved, and a row is found without case. Where a folder tells case apart
    /// the twin that moved is not the recorded file, which is still there and keeps its row and placer (SPEC.md §9.5).
    /// </remarks>
    private bool Gone(StoredFile row) => !File.Exists(_runtime.Files.FilePath(row.File.Folder, row.File.Name));

    /// <summary>Whether a rename report, moved from a path, moved a recorded file rather than a case twin of it.</summary>
    /// <remarks>
    /// Looks and their reports are keyed without case, so where a folder tells case apart a twin's rename can arrive at
    /// the recorded file's look: taken as the recorded file's move, it carried the agent's placer onto the person's file,
    /// which the agent could then remove. A report from the recorded spelling is the recorded file's; one from another
    /// spelling is only where case is not told apart, and there it is the same file.
    /// </remarks>
    private bool MovedTheRecordedFile(string? from, StoredFile row)
    {
        if (from is null)
        {
            return false;
        }

        return string.Equals(Path.GetFileName(from), row.File.Name.Value, StringComparison.Ordinal)
               || !TellsCaseApart(_runtime.Files.FolderPath(row.File.Folder));
    }

    /// <summary>Whether a folder holds names that differ only in case as different files.</summary>
    /// <remarks>
    /// Asked of a file in it with its name's case flipped, which every accepted name changes, as each ends in an
    /// extension of letters. With no file there to ask of, it is taken to tell case apart: the stricter reading, under
    /// which a placer is never carried onto a file it might not be.
    /// </remarks>
    private static bool TellsCaseApart(string directory)
    {
        var names = Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(Path.GetFileName).ToList() : [];

        foreach (var name in names)
        {
            var flipped = new string([.. name!.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]);

            if (!string.Equals(flipped, name, StringComparison.Ordinal))
            {
                return !File.Exists(Path.Combine(directory, flipped)) || names.Contains(flipped, StringComparer.Ordinal);
            }
        }

        return true;
    }

    /// <summary>Whether a path's file and another spelling of its name are two files in its folder.</summary>
    /// <remarks>
    /// Only a folder that tells case apart holds both. Where case is not told apart, a move onto another spelling of a
    /// recorded name replaced that file, and the recorded row is displaced as by any replacement.
    /// </remarks>
    private static bool BothOnDisk(string path, string otherName)
    {
        var fileName = Path.GetFileName(path);

        if (string.Equals(fileName, otherName, StringComparison.Ordinal) || Path.GetDirectoryName(path) is not { } directory)
        {
            return false;
        }

        var names = Directory.EnumerateFiles(directory).Select(Path.GetFileName).ToList();

        return names.Contains(fileName, StringComparer.Ordinal) && names.Contains(otherName, StringComparer.Ordinal);
    }

    /// <summary>Looks at each file left beside a recorded file that has gone, under its name in another case.</summary>
    /// <remarks>
    /// While the recorded file was there such a twin was not in the location; once it is gone the twin is a file of its
    /// own, as the scan at start would find it, and no event at its path need ever come.
    /// </remarks>
    private void LookAtTwins(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);

        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var twin in Directory.EnumerateFiles(directory)
                     .Where(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(Path.GetFileName(p), fileName, StringComparison.Ordinal)))
        {
            Schedule(twin);
        }
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
