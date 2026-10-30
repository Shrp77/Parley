using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// folder a person opens never holds a half-uploaded file, and on the same disk the move
/// is one step. A staged file that is never committed is deleted, and the staging directory is emptied when the
/// broker starts, because nothing survives a broker that could have finished writing it.
/// </para>
/// <para>
/// <b>A download holds its file, and a change through Parley waits for every hold.</b> A read opens the file with
/// no write or delete sharing, so on Windows an editor's save is refused for as long as it runs. A removal or prune
/// asks for the file's gate and waits until the reads in progress finish; a read that arrives
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
    /// <param name="deleteOnDispose">Whether the directories are deleted when this is disposed, which only a test's temporary location wants.</param>
    /// <param name="createStaging">
    /// Opens a staging file for writing. A seam, so a test can run out of disk on demand; <see langword="null"/>
    /// opens a real file.
    /// </param>
    /// <param name="quarantine">
    /// Where a sweep moves placed files whose bytes no longer match, beside the location and never inside it, or
    /// <see langword="null"/> for the location's own path followed by <c>-quarantine</c>.
    /// </param>
    public FileLocationStore(
        string root,
        string incoming,
        bool deleteOnDispose = false,
        Func<string, Stream>? createStaging = null,
        string? quarantine = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(incoming);

        Root = Path.GetFullPath(root);
        Incoming = Path.GetFullPath(incoming);
        Quarantine = Path.GetFullPath(quarantine ?? Root + "-quarantine");
        _deleteOnDispose = deleteOnDispose;
        _createStaging = createStaging
            ?? (path => new FileStream(path, StagingOptions()));
    }

    /// <summary>The location's absolute path.</summary>
    public string Root { get; }

    /// <summary>The staging directory's absolute path.</summary>
    public string Incoming { get; }

    /// <summary>The quarantine directory's absolute path (SPEC.md §9.5).</summary>
    /// <remarks>The person's: Parley moves files into it and never reads, serves, sweeps or deletes anything there.</remarks>
    public string Quarantine { get; }

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

    /// <summary>
    /// Makes sure the location and the staging directory exist as directories of the broker's own, and empties the staging
    /// directory.
    /// </summary>
    /// <remarks>Called when a broker starts: nothing staged by a previous one can still be finished.</remarks>
    public void Prepare()
    {
        // A root already a link when the broker starts is never entered: what it reaches is somebody else's.
        MakeReal(Root);
        MakeReal(Incoming);
        OwnFolder(Root);
        OwnFolder(Incoming);

        if (Directory.Exists(Incoming))
        {
            foreach (var leftover in Directory.EnumerateFiles(Incoming))
            {
                TryDelete(leftover);
            }
        }

        Directory.CreateDirectory(Incoming);
    }

    /// <summary>Makes a folder that only its owner can enter, on Unix, and tightens one that already exists.</summary>
    /// <param name="folder">The location's root, its staging or its quarantine.</param>
    /// <remarks>
    /// The store beside it is the owner's alone (<c>ParleyDatabase</c>), and the shared files are the same agents' hand-offs,
    /// so they are kept the same way: a home folder another local account can list must not hand it the files (lens 2 run
    /// 93). A root nobody else can enter covers every folder and file inside it, those of an older broker included. Windows
    /// keeps the folder's inherited ACL, as the store does.
    /// </remarks>
    private static void OwnFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(folder);

            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        _ = Directory.CreateDirectory(folder, ownerOnly);

        // Never through a link: a mode set on one lands on whatever it leads to.
        if (new DirectoryInfo(folder).LinkTarget is null)
        {
            File.SetUnixFileMode(folder, ownerOnly);
        }
    }

    /// <summary>How a staged upload is created: new, written by this process alone, and on Unix readable by its owner alone.</summary>
    /// <returns>The options.</returns>
    private static FileStreamOptions StagingOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = FileLimits.ChunkBytes,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    /// <summary>Where in the quarantine directory a placed file found at an instant goes.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="nowUtc">When the sweep found it, which names the copy so two never collide.</param>
    /// <returns>A path inside the quarantine directory with <c>/</c> between its parts.</returns>
    /// <remarks>
    /// <b>Never longer than a file system allows one name to be.</b> The stamp adds 20 characters, and a placed name may
    /// be up to 254, so a long one could not be moved, the sweep raised it as locked, and nobody who fetched it was
    /// told (review, lens 1 run 62). Such a name keeps its start and its extension and gains eight characters of its own
    /// hash, so two long names in one sweep never meet; the whole name stays in the problem's path.
    /// </remarks>
    internal static string QuarantineName(string folder, SharedFileName name, DateTimeOffset nowUtc)
    {
        var stamp = nowUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-";
        var room = SharedFileName.MaxPathCharacters - stamp.Length;
        var kept = name.Value;

        if (kept.Length > room)
        {
            var extension = Path.GetExtension(kept);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kept)))[..8];
            kept = kept[..(room - extension.Length - hash.Length - 1)] + "~" + hash + extension;
        }

        return folder + "/" + stamp + kept;
    }

    /// <summary>Moves a placed file whose bytes no longer match what was placed into the quarantine directory.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="movedTo">Where it goes, from <see cref="QuarantineName"/>.</param>
    /// <exception cref="IOException">Another program holds the file, or the move failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The broker may not move it.</exception>
    /// <remarks>
    /// Moved rather than deleted (SPEC.md §9.5), so the person can see what the bytes became and where they may have
    /// spread, and beside the location rather than in it, so nothing Parley serves, lists or sweeps ever reaches it.
    /// </remarks>
    internal void MoveToQuarantine(string folder, SharedFileName name, string movedTo)
    {
        if (IsLinkedFolder(folder))
        {
            throw new IOException($"The folder '{folder}' is a link, so nothing is moved out of it.");
        }

        var into = Path.Combine(Quarantine, folder);

        // Never through a link put in the quarantine directory, or one that is the directory: the file would land
        // wherever it reaches.
        MakeReal(Quarantine);

        if (Directory.Exists(into) && IsLink(into))
        {
            throw new IOException($"The quarantine folder '{folder}' is a link, so nothing is moved into it.");
        }

        OwnFolder(Quarantine);
        Directory.CreateDirectory(into);

        File.Move(AsWritten(FilePath(folder, name)), AsWritten(Path.Combine(into, Path.GetFileName(movedTo))), overwrite: false);
    }

    /// <summary>Deletes everything in the location that is not a recorded file directly in its own recorded folder.</summary>
    /// <param name="kept">Each recorded folder, by the name the store recorded, with the names of its recorded files.</param>
    /// <returns>Each entry that could not be deleted, as a path inside the location, with what stopped it.</returns>
    /// <remarks>
    /// <para>
    /// Called by a sweep inside the store's turn and while it holds every file, so no placement commits and no download
    /// reads while it runs (SPEC.md §9.5). A file put in a folder by hand, a subfolder, a link, a recorded name now
    /// holding something other than a file, and a folder no conversation owns all go; a link goes as a link and is
    /// never entered.
    /// </para>
    /// <para>
    /// A name differing from a recorded one only in case is that name where the disk does not tell case apart, and goes
    /// where it does, since the recorded spelling is then beside it.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<(string Path, string? Folder, Exception Error)> DeleteUnrecorded(
        IReadOnlyDictionary<string, IReadOnlySet<string>> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        var failed = new List<(string, string?, Exception)>();

        // The location itself replaced by a link: the link goes, never what it reaches, and nothing is in it.
        if (IsLinkedRoot(Root))
        {
            try
            {
                MakeReal(Root);
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                failed.Add((Root, null, held));
            }

            return failed;
        }

        if (!Directory.Exists(Root))
        {
            return failed;
        }

        var tops = Entries(AsWritten(Root), failed, null, null);

        foreach (var entry in tops)
        {
            var top = Path.GetFileName(entry);

            if (Kind(entry) is not { } kind)
            {
                // Gone since the folder was read: nothing to delete.
                continue;
            }

            if (kind == EntryKind.Directory && Recorded(top, kept.Keys, tops) is { } recorded)
            {
                var inside = Entries(entry, failed, top, top);

                foreach (var one in inside)
                {
                    var name = Path.GetFileName(one);

                    if (Kind(one) is not { } inner)
                    {
                        continue;
                    }

                    if (!(inner == EntryKind.File && Recorded(name, kept[recorded], inside) is not null))
                    {
                        Remove(one, top + "/" + name, recorded, failed);
                    }
                }
            }
            else
            {
                Remove(entry, top, null, failed);
            }
        }

        return failed;
    }

    // The recorded name an entry on disk is, if any. Names are one name whatever their case (SPEC.md §9.5), so on a disk
    // that does not tell case apart an entry renamed only in case is still the recorded one. On a disk that does, the
    // recorded spelling is there beside it, and the other spelling is a different entry, put there by something else.
    private static string? Recorded(string onDisk, IEnumerable<string> recorded, IReadOnlyCollection<string> beside)
    {
        foreach (var name in recorded)
        {
            if (string.Equals(name, onDisk, StringComparison.Ordinal))
            {
                return name;
            }

            if (string.Equals(name, onDisk, StringComparison.OrdinalIgnoreCase)
                && !beside.Any(b => string.Equals(Path.GetFileName(b), name, StringComparison.Ordinal)))
            {
                return name;
            }
        }

        return null;
    }

    private static List<string> Entries(
        string directory, List<(string, string?, Exception)> failed, string? path, string? folder)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(directory)];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            failed.Add((path ?? ".", folder, unreadable));
            return [];
        }
    }

    private static void Remove(string entry, string path, string? folder, List<(string, string?, Exception)> failed)
    {
        try
        {
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            failed.Add((path, folder, held));
        }
    }

    private enum EntryKind
    {
        File,
        Directory,
        Link,
    }

    // What an entry is, never through a link; null when it is gone since its folder was read. One that cannot be read is
    // a link to the walk, so it is deleted as an entry and never entered.
    private static EntryKind? Kind(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);

            return (attributes & FileAttributes.ReparsePoint) != 0 ? EntryKind.Link
                : (attributes & FileAttributes.Directory) != 0 ? EntryKind.Directory
                : EntryKind.File;
        }
        catch (Exception gone) when (gone is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return EntryKind.Link;
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
    /// committed under either spelling would write over it there. Asked of the disk rather than of the store: a file a
    /// person put in the folder by hand has no row, ever (SPEC.md §9.5), and the disk is the only thing that knows it is
    /// there.
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
        MakeReal(Incoming);

        var path = Path.Combine(Incoming, Guid.NewGuid().ToString("N") + ".part");

        return new StagedFile(this, path, _createStaging(path));
    }

    /// <summary>Holds a file for reading while a download streams it, after any hold over everything has ended.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="cancellationToken">Cancels waiting for a change in progress.</param>
    /// <returns>The hold, carrying the open stream; disposing it ends the hold.</returns>
    /// <exception cref="FileNotFoundException">The file is not on disk.</exception>
    /// <exception cref="IOException">Another program holds the file for writing.</exception>
    /// <exception cref="UnauthorizedAccessException">The broker may not read the file.</exception>
    public async Task<ReadHold> HoldForReadingAsync(string folder, SharedFileName name, CancellationToken cancellationToken)
    {
        // Never through a recorded folder replaced by a link: what it reaches is not the file placed.
        if (IsLinkedFolder(folder))
        {
            throw new FileNotFoundException("The conversation's folder is a link, so the file placed is not there.", name.Value);
        }

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

    /// <summary>Whether a download of a file is waiting for a change to it to finish.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <returns><see langword="true"/> while a reader waits on a change's turn.</returns>
    /// <remarks>
    /// For tests that need a download parked at the turn before they change what it does next: a fixed delay was not long
    /// enough on a slow runner, and the download reached the store after the test had occupied it.
    /// </remarks>
    internal bool IsWaitingToRead(string folder, SharedFileName name) => GateFor(FilePath(folder, name)).Waiting > 0;

    /// <summary>Waits for every download of a file to finish and holds new ones back until the change is made.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The change's turn; disposing it lets reads in again.</returns>
    /// <remarks>
    /// It does not wait out a hold over everything: that hold enters every file's gate there is, so a change to a file is
    /// in turn with it already, and one to a file never changed or read lands either before the destroy's, purge's or
    /// prune's transaction, and goes with it, or after it, refused or placed again as a new file. Waiting would hold every
    /// agent's removal behind one hold.
    /// </remarks>
    public Task<IDisposable> ChangeAsync(string folder, SharedFileName name, CancellationToken cancellationToken) =>
        GateFor(FilePath(folder, name)).EnterChangeAsync(cancellationToken);

    /// <summary>The turn of a conversation's first placement, before it has a folder.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The turn; disposing it lets the next in.</returns>
    /// <remarks>
    /// A first placement has no file's gate to take, so it takes this turn: without it, one could commit between a destroy
    /// reading the conversation's folder (none yet) and destroying it, and leave a folder behind the destroy. Its turn is
    /// one a hold over everything waits for, or waits out.
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
    public void Delete(string folder, SharedFileName name)
    {
        // A recorded folder replaced by a link holds none of its files: what the link reaches is somebody else's, and the
        // sweep deletes the link itself.
        if (!IsLinkedFolder(folder))
        {
            TryDelete(FilePath(folder, name));
        }
    }

    /// <summary>Puts back a file <see cref="MoveToQuarantine"/> moved, when what it moved for could not be recorded.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="movedTo">Where it was moved.</param>
    /// <remarks>So a sweep whose records failed to commit leaves the file where the next sweep finds it, and does it again.</remarks>
    internal void PutBackFromQuarantine(string folder, SharedFileName name, string movedTo) =>
        File.Move(
            AsWritten(Path.Combine(Quarantine, folder, Path.GetFileName(movedTo))),
            AsWritten(FilePath(folder, name)),
            overwrite: false);

    /// <summary>Whether a recorded folder, or the location holding it, is a link (SPEC.md §9.5).</summary>
    /// <param name="folder">The folder name.</param>
    /// <returns><see langword="true"/> when it is a link; never through one is anything read, moved or deleted.</returns>
    internal bool IsLinkedFolder(string folder)
    {
        if (IsLinkedRoot(Root))
        {
            return true;
        }

        try
        {
            var path = FolderPath(folder);

            return Directory.Exists(path) && IsLink(path);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Whether the location, staging or quarantine directory is itself a link, read from the link's own attributes so one
    // reaching nothing counts too.
    private static bool IsLinkedRoot(string root)
    {
        try
        {
            return IsLink(AsWritten(root));
        }
        catch (Exception absent) when (absent is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A root that is a link is removed as a link, its target untouched, and made again as a directory of Parley's own.
    private static void MakeReal(string root)
    {
        if (IsLinkedRoot(root))
        {
            DeleteLink(AsWritten(root));
        }

        Directory.CreateDirectory(root);
    }

    /// <summary>Deletes a conversation's folder and everything in it.</summary>
    /// <param name="folder">The folder name.</param>
    /// <returns><see langword="true"/> when the folder is gone, or the location is a link and holds none of Parley's.</returns>
    public bool DeleteFolder(string folder)
    {
        var path = FolderPath(folder);

        // Only the folder itself, never one its name reaches as a short name: that one is somebody else's. Nor anything in
        // a location that is a link, which holds none of Parley's folders; the next sweep removes the link.
        if (IsLinkedRoot(Root) || !Directory.Exists(Root) || !IsItsOwn(folder))
        {
            return true;
        }

        try
        {
            if (Directory.Exists(path))
            {
                DeleteTree(path);
            }

            return true;
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(path);
        }
    }

    // A tree deleted from a stack of its own, deepest first, rather than one call a level as Directory.Delete makes on
    // Windows: a tree made by hand can be deeper than the stack, and an overflow ends the process. A link is deleted and
    // never entered, since it may lead out of the location.
    private static void DeleteTree(string folder)
    {
        var root = AsWritten(folder);

        // A folder that is itself a link goes as a link, its target untouched.
        if (IsLink(root))
        {
            DeleteLink(root);
            return;
        }

        var pending = new Stack<(string Path, bool Emptied)>();
        pending.Push((root, false));

        while (pending.TryPop(out var top))
        {
            if (top.Emptied)
            {
                Directory.Delete(top.Path, recursive: false);
                continue;
            }

            pending.Push((top.Path, true));

            foreach (var entry in Directory.EnumerateFileSystemEntries(top.Path).ToList())
            {
                var attributes = File.GetAttributes(entry);

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    File.Delete(entry);
                }
                else if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    DeleteLink(entry);
                }
                else
                {
                    pending.Push((entry, false));
                }
            }
        }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    // Windows takes a path exactly as written only with the \\?\ prefix. Without it a trailing dot or space is dropped
    // from every name, so an entry put in by hand as "copied." could be neither read nor deleted, and stopped the delete.
    private static string AsWritten(string path)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    // A junction is removed as a directory on Windows; elsewhere a link to a directory is unlinked.
    private static void DeleteLink(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_deleteOnDispose)
        {
            return;
        }

        foreach (var directory in new[] { Root, Incoming, Quarantine })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    DeleteTree(directory);
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

        // Never a folder made through a location that is a link.
        if (IsLinkedRoot(Root))
        {
            throw new NotItsOwnFolderException(folder);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // **Into the folder itself, or nowhere.** A folder can be recorded before it is made (a placement refused for its
        // length still records it), and on a volume that keeps 8.3 names a later folder can take its name as a short
        // name: writing then put one agent's file in another conversation's folder, listed there as the person's.
        // Nor through a folder that is a link, which may lead out of the location.
        if (!IsItsOwn(folder) || IsLink(FolderPath(folder)))
        {
            throw new NotItsOwnFolderException(folder);
        }

        // **Never over a file** (SPEC.md §9.5). A placed file is never changed, so a placement writes only a name nothing
        // holds on disk; one put there by hand since the placement looked is refused, with nothing written.
        try
        {
            File.Move(stagedPath, target, overwrite: false);
        }
        catch (IOException taken) when (File.Exists(target))
        {
            throw new Grpc.Core.RpcException(
                new Grpc.Core.Status(
                    Grpc.Core.StatusCode.Aborted,
                    "A file was put under this name on disk while the placement arrived, so nothing was written. Place it "
                    + "under another name."),
                taken.Message);
        }
    }

    /// <summary>Whether a file on disk, if there is one, is the one the store recorded under its name.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="recorded">The size and write time recorded.</param>
    /// <returns><see langword="true"/> when no file is there, or the one there has the recorded size and time.</returns>
    /// <remarks>
    /// For a removal by the agent that placed it: a file there that is not the one placed was changed or put there by
    /// hand, and may be the person's. Removing it would delete bytes the agent never placed (SPEC.md §9.5).
    /// </remarks>
    internal bool HoldsTheRecordedFileOrNone(string folder, SharedFileName name, (long SizeBytes, long ModifiedTicks) recorded)
        => IsLinkedFolder(folder) || HoldsTheRecordedFileOrNoneAt(FilePath(folder, name), recorded);

    /// <summary>Whether a file is on disk, is not, or cannot be told.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <param name="name">The file's name.</param>
    /// <returns>
    /// <see langword="true"/> when it is there, <see langword="false"/> when it is not, and <see langword="null"/> when
    /// the disk would not say, as for a folder that cannot be read.
    /// </returns>
    /// <remarks>
    /// Three answers, because a recorded file missing from disk is recorded gone (SPEC.md §9.5) and a file in a folder
    /// that cannot be read is not missing. <see cref="File.Exists(string)"/> answers false for both, so it would record
    /// gone, for good, every file in a folder the broker merely cannot read for now.
    /// </remarks>
    internal bool? IsOnDisk(string folder, SharedFileName name)
    {
        // A recorded folder replaced by a link: the file placed is not there, whatever the link reaches.
        if (IsLinkedFolder(folder))
        {
            return false;
        }

        try
        {
            _ = File.GetAttributes(FilePath(folder, name));
            return true;
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception unreadable) when (unreadable is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool HoldsTheRecordedFileOrNoneAt(string path, (long SizeBytes, long ModifiedTicks) recorded)
    {
        var info = new FileInfo(path);

        // A row whose time was never recorded is compared by size alone.
        return !info.Exists
               || (info.Length == recorded.SizeBytes
                   && (recorded.ModifiedTicks == 0 || info.LastWriteTimeUtc.Ticks == recorded.ModifiedTicks));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Left for the next sweep, or already gone.
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

        /// <summary>Moves the finished file into its folder, under a name nothing holds on disk.</summary>
        /// <param name="folder">The conversation's folder name.</param>
        /// <param name="name">The file's name.</param>
        /// <returns>The last-write time the committed file carries.</returns>
        /// <exception cref="Grpc.Core.RpcException">
        /// <c>Aborted</c>: a file was put under the name on disk meanwhile; nothing was written.
        /// </exception>
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

    /// <summary>
    /// A conversation's folder that is not its own on disk: another folder's short name, or a link put there by hand.
    /// </summary>
    /// <param name="folder">The folder the store recorded.</param>
    internal sealed class NotItsOwnFolderException(string folder)
        : IOException($"The folder '{folder}' is another folder's short name on this volume, or a link.");

    private sealed class Reopening(Action reopen) : IDisposable
    {
        public void Dispose() => reopen();
    }

    /// <summary>Readers and one change at a time for one file, with a waiting change holding new readers back.</summary>
    private sealed class Gate
    {
        private readonly object _lock = new();
        private int _readers;
        private int _waiting;
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

        public int Waiting
        {
            get
            {
                lock (_lock)
                {
                    return _waiting;
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
                    _waiting++;
                }

                try
                {
                    await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock)
                    {
                        _waiting--;
                    }
                }
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
