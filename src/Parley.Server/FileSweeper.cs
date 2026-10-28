using Parley.Core;
using Parley.Store;

namespace Parley.Server;

/// <summary>
/// Checks the file location against the store and puts right what something other than Parley did to it (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Run when the broker starts, and whenever a list, fetch, removal or placement finds a file missing, changed, or put
/// there by hand.</b> Nothing outside Parley should touch the location, so anything that did is put right whole rather
/// than one file at a time: a recorded file whose bytes no longer hash to what was placed is moved to quarantine and
/// its fetchers and placer are told it was tampered with; a recorded file missing is recorded gone; and everything
/// else on disk that no row names is deleted. What another program holds, so it cannot be read, moved or deleted, is
/// left and raised as a problem for the person.
/// </para>
/// <para>
/// <b>Hashed outside the store's turn, settled inside it.</b> Hashing every file is the slow part and reads only, so
/// agents are not held up by it. What it found is then checked again inside one turn of the store, holding every file
/// as a destroy does, so no placement commits and no download reads while the sweep moves and deletes, and a file
/// put right since the hashing is judged as it is now; one removed, or removed and placed again, is left to the next
/// sweep to judge against its own row.
/// </para>
/// <para>
/// One sweep at a time. One asked for while another runs runs once more after it, so a sweep that began before the
/// thing it was asked about is never the last.
/// </para>
/// </remarks>
public sealed class FileSweeper : IAsyncDisposable
{
    private readonly BrokerRuntime _runtime;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();

    /// <summary>Set once disposal has begun.</summary>
    /// <remarks>
    /// A runtime is disposed twice when a test stops its broker early and its fixture stops it again, and a second
    /// <see cref="CancellationTokenSource.Cancel()"/> on a disposed source throws.
    /// </remarks>
    private int _disposed;
    private Task _running = Task.CompletedTask;
    private bool _again;

    // Whether a run is going or about to, set and cleared only under the gate: a task's own completion is seen late, so
    // an ask that read it in the moment between the run's last look and its end started nothing, and was lost.
    private bool _active;

    internal FileSweeper(BrokerRuntime runtime) => _runtime = runtime;

    /// <summary>Gets or sets what is told about a sweep that failed for a reason other than a held entry.</summary>
    /// <remarks>Set by the server to its log. A failed sweep keeps what it finished, and the next one tries again.</remarks>
    public Action<Exception>? Failed { get; set; }

    /// <summary>Asks for a sweep, without waiting for it.</summary>
    public void Ask()
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _again = true;

            if (!_active)
            {
                _active = true;
                _running = Task.Run(RunAsync);
            }
        }
    }

    /// <summary>Waits until no sweep is running or asked for.</summary>
    /// <returns>A task that completes when the sweeper is idle.</returns>
    /// <remarks>For a caller that must see what a sweep it caused did, a test among them.</remarks>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task running;

            lock (_gate)
            {
                running = _running;
            }

            await running.ConfigureAwait(false);

            lock (_gate)
            {
                if (!_active)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Sweeps now and waits for it.</summary>
    /// <param name="cancellationToken">Cancels the hashing, and the rest of the sweep between entries.</param>
    /// <returns>A task that completes when the sweep has finished.</returns>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await SweepOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Task running;

        lock (_gate)
        {
            _stopping.Cancel();
            running = _running;
        }

        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
        _one.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_again || _stopping.IsCancellationRequested)
                {
                    _active = false;
                    return;
                }

                _again = false;
            }

            try
            {
                await SweepAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _active = false;
                }

                return;
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                Failed?.Invoke(failed);
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        var files = _runtime.Files;
        var recorded = await _runtime.UseConnectionAsync(
            (connection, _, ct) => new SharedFileRepository(connection).GetAllAsync(cancellationToken: ct),
            cancellationToken).ConfigureAwait(false);

        // What each recorded file is on disk, read without holding anything.
        var looked = new List<(StoredFile File, Seen Seen)>(recorded.Count);

        foreach (var stored in recorded)
        {
            looked.Add((stored, await LookAsync(files, stored, cancellationToken).ConfigureAwait(false)));
        }

        using var everything = await new FileWork(_runtime).HoldEverythingAsync(cancellationToken).ConfigureAwait(false);

        // Inside the turn nothing is cancelled part way: an entry moved to quarantine and not recorded would be a file set
        // aside with nobody told. A stop is seen between entries, and the next sweep does the rest.
        _ = await _runtime.UseConnectionAsync(
            async (connection, now, unused) =>
            {
                var ct = CancellationToken.None;
                var shared = new SharedFileRepository(connection);
                var problems = new FileProblemRepository(connection);

                foreach (var (stored, first) in looked)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    // The row as it is now: the name may have been removed and placed again since the hashing, and the file
                    // there then is that placement's, judged by the next sweep against its own row.
                    var current = await shared.GetAsync(stored.File.ConversationId, stored.File.Name, cancellationToken: ct)
                        .ConfigureAwait(false);

                    if (current is null || current.File.Sha256 != stored.File.Sha256 || current.File.PlacedUtc != stored.File.PlacedUtc)
                    {
                        continue;
                    }

                    // Looked at again: the hashing ran outside the turn, and the file may have been put right since.
                    var seen = first.Kind == SeenKind.Placed ? first : await LookAsync(files, stored, ct).ConfigureAwait(false);
                    var path = stored.File.Folder + "/" + stored.File.Name.Value;

                    switch (seen.Kind)
                    {
                        case SeenKind.Missing:
                            _ = await shared.RemoveMissingAsync(stored, now, cancellationToken: ct).ConfigureAwait(false);
                            break;

                        case SeenKind.Held:
                            _ = await problems.RaiseAsync(
                                FileProblemKind.Locked, stored.File.ConversationId, path, null, null, null,
                                Held(seen.Error!), now, cancellationToken: ct).ConfigureAwait(false);
                            break;

                        case SeenKind.Tampered:
                            await QuarantineAsync(connection, stored, seen, path, now, ct).ConfigureAwait(false);
                            break;
                    }
                }

                // Everything left on disk that no row names, read again now that the removals above are made. A recorded
                // folder holding no file is kept too: a folder keeps its name while it is empty.
                var remaining = await shared.GetAllAsync(cancellationToken: ct).ConfigureAwait(false);
                var folders = await shared.GetFoldersAsync(cancellationToken: ct).ConfigureAwait(false);
                var kept = folders.ToDictionary(
                    f => f.Folder,
                    f => (IReadOnlySet<string>)remaining
                        .Where(r => r.File.Folder == f.Folder)
                        .Select(r => r.File.Name.Value)
                        .ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);

                foreach (var (entry, folder, error) in files.DeleteUnrecorded(kept))
                {
                    _ = await problems.RaiseAsync(
                        FileProblemKind.Locked, folders.FirstOrDefault(f => f.Folder == folder).ConversationId, entry,
                        null, null, null, Held(error), now, cancellationToken: ct).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task QuarantineAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        StoredFile stored,
        Seen seen,
        string path,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var problems = new FileProblemRepository(connection);
        var movedTo = FileLocationStore.QuarantineName(stored.File.Folder, stored.File.Name, now);
        Exception error;

        // **The records and the move are one act.** The row, the notices and the problem are written in a transaction
        // that commits only once the file has moved, so a move that fails leaves everything as it was, and nothing is
        // recorded quarantined that is still in the location.
        using (var scope = WriteScope.Begin(connection, null))
        {
            if (!await new SharedFileRepository(connection)
                    .RemoveTamperedAsync(stored, seen.Found!.Value, seen.Bytes, now, scope.Transaction, cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            _ = await problems.RaiseAsync(
                FileProblemKind.Quarantined, stored.File.ConversationId, path, stored.File.Sha256, seen.Found, movedTo,
                "Its bytes no longer match what was placed, so something other than Parley changed it. It was moved to "
                + "quarantine and every agent that fetched or placed it was told it is no longer reliable.",
                now, scope.Transaction, cancellationToken).ConfigureAwait(false);

            try
            {
                _runtime.Files.MoveToQuarantine(stored.File.Folder, stored.File.Name, movedTo);
                scope.Complete();
                return;
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                // Rolled back below: left where it is and still recorded. A fetch hashes what it streams, so it is still
                // never served.
                error = held;
            }
        }

        _ = await problems.RaiseAsync(
            FileProblemKind.Locked, stored.File.ConversationId, path, stored.File.Sha256, seen.Found, null,
            "Its bytes no longer match what was placed, but it could not be moved to quarantine, so it stays where it "
            + "is and is never served: " + error.Message,
            now, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // What a recorded file is on disk now.
    private static async Task<Seen> LookAsync(FileLocationStore files, StoredFile stored, CancellationToken cancellationToken)
    {
        var path = files.FilePath(stored.File.Folder, stored.File.Name);
        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Seen(SeenKind.Missing, null, 0, null);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return new Seen(SeenKind.Held, null, 0, unreadable);
        }

        // Something else under the name, a folder or a link, is not the file placed: it is gone, and what took its place
        // is deleted with everything else no row names.
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return new Seen(SeenKind.Missing, null, 0, null);
        }

        try
        {
            var hashed = await HashHeldAsync(path, cancellationToken).ConfigureAwait(false);

            return hashed.Sha256 == stored.File.Sha256
                ? new Seen(SeenKind.Placed, null, 0, null)
                : new Seen(SeenKind.Tampered, hashed.Sha256, hashed.Bytes, null);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            return new Seen(SeenKind.Held, null, 0, held);
        }
    }

    // Read with write sharing refused, so a program holding the file to write it is found holding it rather than read
    // half way through a change.
    private static async Task<(ContentHash Sha256, long Bytes)> HashHeldAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, FileLimits.ChunkBytes, useAsync: true);

        await using var _ = stream.ConfigureAwait(false);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[FileLimits.ChunkBytes];
        long size = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            size += read;
        }

        return (ContentHash.FromDigest(hash.GetHashAndReset()), size);
    }

    private static string Held(Exception error) =>
        "The sweep could not read, move or delete it, usually because another program holds it: " + error.Message;

    private enum SeenKind
    {
        Placed,
        Missing,
        Held,
        Tampered,
    }

    private readonly record struct Seen(SeenKind Kind, ContentHash? Found, long Bytes, Exception? Error);
}
