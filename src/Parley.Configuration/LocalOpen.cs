using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Parley.Core;

namespace Parley.Configuration;

/// <summary>What opening a path on this machine came to.</summary>
public enum LocalOpenStatus
{
    /// <summary>Opened.</summary>
    Opened,

    /// <summary>Nothing of that kind is there.</summary>
    Missing,

    /// <summary>The path, or a link along it, leads off this machine, so it was never looked up.</summary>
    OffThisMachine,

    /// <summary>The file itself is a link and was asked for as one that may not be, as a file about to be written: it was
    /// not followed.</summary>
    Changed,

    /// <summary>Another process holds the file in a way the open does not allow.</summary>
    Busy,

    /// <summary>It could not be opened for another reason.</summary>
    Unreadable,

    /// <summary>Something is there that is neither a file nor a folder: a pipe, a socket or a device. It was not read,
    /// since opening or reading one can wait for ever.</summary>
    NotAFile,
}

/// <summary>
/// Opens a path somebody else chose (an agent's reported workspace and its launch configuration) without ever following a
/// link off this machine, however the path changes while it is opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>A check and then an ordinary open is a race an agent wins.</b> <see cref="OffMachineLinks"/> reads every part of a
/// path and the links along it, and refuses one that leads off this machine; an ordinary open afterwards follows whatever
/// links stand at that moment. An agent flipping its own folder, or its <c>.mcp.json</c>, between a real one and a link to
/// <c>\\host\share</c> landed in that gap within seconds, and Windows offered the person's sign-in to the host: on a
/// retire or purge, and with Auto Accept on no act of the person's at all (review, lens 2 round 13).
/// </para>
/// <para>
/// <b>So on Windows the path is opened one part at a time, each relative to the folder already open, and no part is ever
/// followed by Windows.</b> Each part is opened as itself (<c>FILE_OPEN_REPARSE_POINT</c>); a link is followed only by
/// reading its target from the link's own handle and walking on from there under <see cref="OffMachineLinks"/>' rules, so
/// the walk and the open are one act with no gap between them. The disk is opened by its device name
/// (<c>\Device\HarddiskVolume3\</c>) read at that moment, so a drive letter redefined meanwhile redirects nothing. A file
/// about to be written is never itself a link (<see cref="LocalOpenStatus.Changed"/>). A mount point with no drive letter
/// is refused: its folder has no name a walk could start again from, and such a folder in a repository is rare.
/// </para>
/// <para>
/// Elsewhere a share is a mount, already connected, so following a link to it offers no sign-in: the walk is kept and the
/// open is an ordinary one, except that a file about to be written is still refused when it is itself a link.
/// </para>
/// <para>
/// <b>Elsewhere only an ordinary file is opened, and the open never waits.</b> An agent can make its <c>.mcp.json</c>, or
/// a link to one, a named pipe; an ordinary open of a pipe waits for a writer, cannot be cancelled, and the node reads
/// every catalogued repository's file one after another, so one agent froze its machine's node for every agent (review,
/// lens 2, Linux). See <see cref="NotAFile"/>.
/// </para>
/// </remarks>
public static class LocalOpen
{
    private const uint Synchronize = 0x00100000;
    private const uint ReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 1;
    private const uint SynchronousIo = 0x20;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint CaseInsensitive = 0x40;
    private const uint ReparseAttribute = 0x400;
    private const uint DirectoryAttribute = 0x10;
    private const int AttributeTagInfo = 9;

    private const uint NameNotFound = 0xC0000034;
    private const uint NameCollision = 0xC0000035;
    private const uint Delete = 0x00010000;
    private const int DispositionInfo = 4;
    private const uint CreateNew = 2;
    private const uint NonDirectory = 0x40;
    private const uint PathNotFound = 0xC000003A;
    private const uint SharingViolation = 0xC0000043;
    private const uint IsADirectory = 0xC00000BA;
    private const uint NotADirectory = 0xC0000103;

    // No FILE_TRAVERSE: a relative open needs none on its parent, and asking for it failed on a folder whose permissions
    // deny traverse, which Windows itself walks past (review, lens 1 round 12), and counted against another program's
    // share of a file.
    private const uint FolderAccess = Synchronize | ReadAttributes;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>Opens a file that must be on this machine, following no link to anywhere else, however the path changes.</summary>
    /// <param name="path">The path, as reported.</param>
    /// <param name="access">What the handle may do.</param>
    /// <param name="share">What others may do while it is open.</param>
    /// <param name="followLink">Whether the file itself may be a link to a file on this machine; a file about to be
    /// written never may, as a launch configuration that is a link has always been refused for writing.</param>
    /// <returns>What came of it, and the open file when it was opened; the caller disposes it.</returns>
    /// <remarks>A file that is a link and may not be is <see cref="LocalOpenStatus.Changed"/>. Links on the way to it are
    /// followed when they lead to this machine.</remarks>
    public static (LocalOpenStatus Status, FileStream? Stream) File(
        string path, FileAccess access, FileShare share, bool followLink = false)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!OperatingSystem.IsWindows())
        {
            if (OffMachineLinks.LeadsOffThisMachine(path, out var reached))
            {
                return (LocalOpenStatus.OffThisMachine, null);
            }

            // The walk resolved every link, the file's own included, so whether the file itself is one is asked of it.
            // Elsewhere a share is a mount and asking offers no sign-in, so this one lookup by path is safe here.
            if (!followLink)
            {
                try
                {
                    if (new FileInfo(OffMachineLinks.Typed(path)).LinkTarget is not null)
                    {
                        return (LocalOpenStatus.Changed, null);
                    }
                }
                catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
                {
                    return (LocalOpenStatus.Unreadable, null);
                }
            }

            return Ordinary(reached, access, share, followLink);
        }

        var wanted = Synchronize | ReadAttributes
            | ((access & FileAccess.Read) != 0 ? GenericRead : 0)
            | ((access & FileAccess.Write) != 0 ? GenericWrite : 0);

        var (status, handle, _) = Resolve(
            OffMachineLinks.Typed(path), 0, directory: false, followLink, wanted, (uint)share & ShareAll);

        if (status != LocalOpenStatus.Opened)
        {
            return (status, null);
        }

        try
        {
            return (status, new FileStream(handle!, access, 4096, isAsync: false));
        }
        catch
        {
            handle!.Dispose();
            throw;
        }
    }

    /// <summary>Whether a file that must be on this machine is itself a link, asked of its attributes alone.</summary>
    /// <param name="path">The path, as written.</param>
    /// <returns><see cref="LocalOpenStatus.Changed"/> when it is a link; <see cref="LocalOpenStatus.Unreadable"/> when not
    /// even its attributes can be read, which a link's own permissions can cause.</returns>
    /// <remarks>Attributes only, so a file this account may not read is still asked: denied reading is not a link.</remarks>
    public static LocalOpenStatus Itself(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!OperatingSystem.IsWindows())
        {
            if (OffMachineLinks.LeadsOffThisMachine(path, out _))
            {
                return LocalOpenStatus.OffThisMachine;
            }

            try
            {
                var info = new FileInfo(OffMachineLinks.Typed(path));

                return info.LinkTarget is not null ? LocalOpenStatus.Changed
                    : info.Exists ? LocalOpenStatus.Opened : LocalOpenStatus.Missing;
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            {
                return LocalOpenStatus.Unreadable;
            }
        }

        var (status, handle, _) = Resolve(
            OffMachineLinks.Typed(path), 0, directory: false, followLast: false, Synchronize | ReadAttributes, ShareAll);

        handle?.Dispose();

        return status;
    }

    /// <summary>Where a path that must be on this machine leads, every link followed as <see cref="File"/> follows them.</summary>
    /// <param name="path">The path, as written.</param>
    /// <param name="reached">Where it leads; the parts past what is there are kept as written.</param>
    /// <returns><see cref="LocalOpenStatus.OffThisMachine"/> when it, or a link along it, leads off this machine or cannot
    /// be read; otherwise whether something is there.</returns>
    /// <remarks>
    /// For <see cref="LocalStorage"/>, which judges where a folder really is: the walk is the open, so a folder flipped to a
    /// link while it is judged is never followed, as <see cref="OffMachineLinks.LeadsOffThisMachine(string?)"/> alone
    /// could be (review, lens 3 round 10).
    /// </remarks>
    public static LocalOpenStatus Reach(string path, out string reached)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!OperatingSystem.IsWindows())
        {
            if (OffMachineLinks.LeadsOffThisMachine(path, out reached))
            {
                return LocalOpenStatus.OffThisMachine;
            }

            return Directory.Exists(reached) || System.IO.File.Exists(reached) ? LocalOpenStatus.Opened : LocalOpenStatus.Missing;
        }

        var (status, handle, at) = Resolve(OffMachineLinks.Typed(path), 0, directory: true, followLast: true, FolderAccess, ShareAll);

        handle?.Dispose();
        reached = at;

        return status switch
        {
            LocalOpenStatus.Unreadable or LocalOpenStatus.Changed => LocalOpenStatus.OffThisMachine,
            LocalOpenStatus.Busy => LocalOpenStatus.Opened,
            _ => status,
        };
    }

    /// <summary>Whether a folder that must be on this machine is there, following no link to anywhere else.</summary>
    /// <param name="path">The path, as reported.</param>
    /// <returns><see cref="LocalOpenStatus.Opened"/> when it is a folder that is there.</returns>
    public static LocalOpenStatus Folder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!OperatingSystem.IsWindows())
        {
            return OffMachineLinks.LeadsOffThisMachine(path, out var reached)
                ? LocalOpenStatus.OffThisMachine
                : Directory.Exists(reached) ? LocalOpenStatus.Opened : LocalOpenStatus.Missing;
        }

        var (status, handle, _) = Resolve(OffMachineLinks.Typed(path), 0, directory: true, followLast: true, FolderAccess, ShareAll);

        handle?.Dispose();

        return status;
    }

    /// <summary>Reads a file that must be on this machine whole, as <see cref="File"/> opens it, following a link to a file
    /// here.</summary>
    /// <param name="path">The path.</param>
    /// <param name="json">Whether it is JSON, read within <see cref="StrictJson.MostBytes"/>; otherwise prose, read whole.</param>
    /// <returns>What came of it, and the text when it was read; no text when nothing is there or it leads elsewhere.</returns>
    /// <exception cref="System.Text.Json.JsonException">JSON longer than the bound.</exception>
    /// <exception cref="IOException">Something is there and could not be read: held by another program, or denied. Thrown
    /// rather than answered as no text, which would read as no file (a plan saying <i>create</i> for one that exists). A
    /// pipe, a socket or a device is refused this way too, saying so (<see cref="NotAFileProblem"/>).</exception>
    public static (LocalOpenStatus Status, string? Text) ReadText(string path, bool json)
    {
        var (status, stream) = File(path, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, followLink: true);

        using (stream)
        {
            if (status == LocalOpenStatus.NotAFile)
            {
                throw new IOException(NotAFileProblem(path));
            }

            if (status is LocalOpenStatus.Busy or LocalOpenStatus.Unreadable)
            {
                throw new IOException($"{path} is there and could not be read.");
            }

            if (status != LocalOpenStatus.Opened)
            {
                return (status, null);
            }

            if (json)
            {
                return (status, StrictJson.Read(stream!));
            }

            using var reader = new StreamReader(stream!, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

            return (status, reader.ReadToEnd());
        }
    }

    /// <summary>Creates a file that must not exist yet, in a folder that must be on this machine.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The new file, open for writing and held exclusively; the caller disposes it.</returns>
    /// <exception cref="IOException">Something is already there, the folder is missing, or it could not be made.</exception>
    /// <exception cref="InvalidDataException">The folder leads off this machine.</exception>
    /// <remarks>
    /// The folder is walked and opened as <see cref="Folder"/> walks it, and the file is created relative to that open
    /// folder, so a folder an agent swaps for a link to a share between the walk and the create is not followed: a first
    /// enrolment created <c>.mcp.json</c> by path after the walk found none (review, lens 3 round 13). Nothing already
    /// there is replaced, a link included.
    /// </remarks>
    public static FileStream Create(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var full = OffMachineLinks.FullPath(OffMachineLinks.Typed(path));
        var folder = Path.GetDirectoryName(full) ?? throw new IOException($"{path} names no folder.");
        var name = Path.GetFileName(full);

        if (!OperatingSystem.IsWindows())
        {
            return OffMachineLinks.LeadsOffThisMachine(folder, out _)
                ? throw new InvalidDataException($"{folder} leads to another machine, and Parley does not look up a path there")
                : new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        var (status, parent, _) = Resolve(folder, 0, directory: true, followLast: true, FolderAccess, ShareAll);

        if (status == LocalOpenStatus.OffThisMachine)
        {
            throw new InvalidDataException($"{folder} leads to another machine, and Parley does not look up a path there");
        }

        if (status != LocalOpenStatus.Opened)
        {
            parent?.Dispose();

            throw new IOException($"{folder} is not a folder that could be opened.");
        }

        using (parent)
        {
            // DELETE as well, so a create that fails can be undone through this handle (Discard), never by path.
            var (made, handle) = Open(parent, name, Synchronize | ReadAttributes | GenericWrite | Delete, 0, CreateNew);

            if (made != LocalOpenStatus.Opened)
            {
                throw new IOException(made == LocalOpenStatus.Busy ? $"{full} already exists." : $"{full} could not be made.");
            }

            try
            {
                return new FileStream(handle!, FileAccess.Write, 4096, isAsync: false);
            }
            catch
            {
                handle!.Dispose();
                throw;
            }
        }
    }

    /// <summary>Removes a file <see cref="Create"/> made, through its own handle, and closes it.</summary>
    /// <param name="created">The file <see cref="Create"/> returned.</param>
    /// <param name="path">Its path, used only off Windows.</param>
    /// <remarks>
    /// A failed first write was undone by deleting the path after closing, and a folder swapped for a link meanwhile would
    /// have the delete follow it, to a share or to another repository's file of that name (review, lens 2 round 18, lens 3
    /// round 14). Through the handle, only the file this call made can go.
    /// </remarks>
    public static void Discard(FileStream created, string path)
    {
        ArgumentNullException.ThrowIfNull(created);

        if (!OperatingSystem.IsWindows())
        {
            // Removed before the close: a close flushes what a failed write left in the buffer, throws again on a full
            // disk, and the delete after it was skipped, leaving an empty file (review, lens 1 round 18).
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {
                // Left for the person: nothing else here may remove it.
            }

            try
            {
                created.Dispose();
            }
            catch (IOException)
            {
                // The file is gone; the buffer it held has nowhere to go.
            }

            return;
        }

        try
        {
            _ = SetFileInformationByHandle(created.SafeFileHandle, DispositionInfo, [1], 1);
        }
        finally
        {
            created.Dispose();
        }
    }

    /// <summary>Whether a folder that must be on this machine is there, this account allowed to open it or not.</summary>
    /// <param name="path">The path.</param>
    /// <returns><see langword="true"/> when a folder is there, as <c>Directory.Exists</c> answered, found as
    /// <see cref="Folder"/> finds it.</returns>
    public static bool FolderThere(string path) =>
        Folder(path) is LocalOpenStatus.Opened or LocalOpenStatus.Busy or LocalOpenStatus.Unreadable;

    /// <summary>Whether a file or a folder that must be on this machine is there, following links to things here.</summary>
    /// <param name="path">The path.</param>
    /// <returns><see cref="LocalOpenStatus.Opened"/> when something is there.</returns>
    public static LocalOpenStatus Entry(string path)
    {
        // Something there that this account cannot open, or another holds, is still there: a `.git` folder nobody may list
        // is a repository all the same, as Directory.Exists said.
        static LocalOpenStatus There(LocalOpenStatus status) =>
            status is LocalOpenStatus.Busy or LocalOpenStatus.Unreadable or LocalOpenStatus.NotAFile
                ? LocalOpenStatus.Opened
                : status;

        var folder = There(Folder(path));

        if (folder != LocalOpenStatus.Missing)
        {
            return folder;
        }

        var (status, stream) = File(path, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, followLink: true);

        stream?.Dispose();

        return There(status);
    }

    /// <summary>Whether something at a path is there and is neither a file nor a folder: a pipe, a socket or a device.</summary>
    /// <param name="path">The path; a link is followed, as reading it would.</param>
    /// <returns><see langword="true"/> only when something is there and is of none of the ordinary kinds. Always
    /// <see langword="false"/> on Windows, where no such thing has a path in a folder.</returns>
    /// <remarks>
    /// A question asked of a path, so it is only a description to show the person before anything is read; the read itself
    /// is refused again through its own open (<see cref="File"/>), since the path can change in between.
    /// </remarks>
    public static bool NotAFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        var status = new byte[StatusBytes];

        try
        {
            return Stat(path, status) == 0 && KindOf(status) is not (RegularKind or FolderKind);
        }
        catch (Exception missing) when (missing is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The sentence a refused pipe, socket or device is reported with.</summary>
    /// <param name="path">The path that was not read.</param>
    /// <returns>One sentence the person can act on.</returns>
    public static string NotAFileProblem(string path) =>
        $"{path} is not an ordinary file (it is a pipe, a socket or a device), so Parley did not read it.";

    // Off Windows: only an ordinary file, opened without ever waiting (the class's remarks).
    //
    // .NET says nothing of a file's kind: FileAttributes reads Normal for a pipe and for /dev/null alike, by path and by
    // handle, and its open of a pipe waits for a writer with no way to cancel it. So the open is libc's own, with
    // O_NONBLOCK, which returns at once for a pipe; the kind is then read from the open handle, so a pipe swapped in after
    // any check by path is still the thing refused. A socket refuses the open itself (ENXIO).
    //
    // The kind is read through .NET's own native shim (SystemNative_FStat), not libc's fstat: its FileStatus is the one
    // structure .NET itself reads to refuse a folder, laid out the same on every platform .NET ships, where libc's struct
    // stat differs between Linux x64, Linux arm64 and macOS (and macOS x64 has two), and glibc before 2.33 exports no
    // fstat at all. The shim ships in the runtime this runs on, so its layout is the runtime's own; a runtime without it
    // throws, which is refused as unreadable rather than read. The flag values are the only per-platform knowledge here,
    // and only Linux and macOS are listed; any other system keeps the ordinary open.
    //
    // FileShare is kept as .NET keeps it off Windows: an advisory flock, exclusive for FileShare.None and shared otherwise,
    // and a file another holds exclusively is Busy, so the read-modify-write in enrolment still excludes these readers.
    private static (LocalOpenStatus, FileStream?) Ordinary(string reached, FileAccess access, FileShare share, bool followLink)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return Ordinary(reached, access, share);
        }

        int descriptor;
        int error;

        try
        {
            do
            {
                descriptor = OpenNative(reached, OpenFlags(access, followLink));
                error = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (descriptor < 0 && error == Interrupted);
        }
        catch (Exception missing) when (missing is DllNotFoundException or EntryPointNotFoundException)
        {
            return (LocalOpenStatus.Unreadable, null);
        }

        if (descriptor < 0)
        {
            return (error switch
            {
                NoSuchEntry or NotAFolder or IsAFolder => LocalOpenStatus.Missing,
                NoDevice => LocalOpenStatus.NotAFile,
                TextBusy => LocalOpenStatus.Busy,
                _ when error == TooManyLinks && !followLink => LocalOpenStatus.Changed,
                _ => LocalOpenStatus.Unreadable,
            }, null);
        }

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);

        try
        {
            var status = new byte[StatusBytes];

            if (FStat(handle, status) != 0)
            {
                handle.Dispose();

                return (LocalOpenStatus.Unreadable, null);
            }

            switch (KindOf(status))
            {
                case FolderKind:
                    // A folder where a file was asked for is no file, as on Windows.
                    handle.Dispose();

                    return (LocalOpenStatus.Missing, null);
                case not RegularKind:
                    handle.Dispose();

                    return (LocalOpenStatus.NotAFile, null);
            }

            if (Flock(handle, (share == FileShare.None ? LockExclusive : LockShared) | LockNoWait) != 0
                && Marshal.GetLastPInvokeError() == WouldBlock)
            {
                handle.Dispose();

                return (LocalOpenStatus.Busy, null);
            }

            return (LocalOpenStatus.Opened, new FileStream(handle, access, 4096, isAsync: false));
        }
        catch (Exception missing) when (missing is DllNotFoundException or EntryPointNotFoundException)
        {
            handle.Dispose();

            return (LocalOpenStatus.Unreadable, null);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // O_RDONLY, O_WRONLY and O_RDWR are 0, 1 and 2 everywhere; the rest differ. Linux: O_NONBLOCK 0x800 and O_CLOEXEC
    // 0x80000 on every architecture .NET runs on there, O_NOFOLLOW 0x8000 on Arm and PowerPC and 0x20000 on the rest
    // (x64, x86, s390x, RISC-V, LoongArch). macOS: 0x4, 0x1000000 and 0x100.
    //
    // O_NOCTTY (Linux 0x100, macOS 0x20000) always: a session leader with no controlling terminal that opens a terminal
    // without it takes that terminal on, and `parley serve` under its autostart unit is one. An agent that pointed its launch
    // file at a pseudo-terminal it held, and then closed its end, sent the broker SIGHUP and ended it for every agent on the
    // machine (review, lens 2 run A on fc26cb8a, Linux). The read is refused as not an ordinary file either way.
    private static int OpenFlags(FileAccess access, bool followLink)
    {
        var mode = access switch
        {
            FileAccess.Read => 0,
            FileAccess.Write => 1,
            _ => 2,
        };

        if (OperatingSystem.IsMacOS())
        {
            return mode | 0x4 | 0x1000000 | 0x20000 | (followLink ? 0 : 0x100);
        }

        var noFollow = RuntimeInformation.ProcessArchitecture
            is Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le
            ? 0x8000
            : 0x20000;

        return mode | 0x800 | 0x80000 | 0x100 | (followLink ? 0 : noFollow);
    }

    private static int KindOf(byte[] status) => BitConverter.ToInt32(status, 4) & KindMask;

    // The errno values that are the same on Linux and macOS, and the two that are not.
    private const int NoSuchEntry = 2;
    private const int Interrupted = 4;
    private const int NoDevice = 6;
    private const int NotAFolder = 20;
    private const int IsAFolder = 21;
    private const int TextBusy = 26;

    private static int TooManyLinks => OperatingSystem.IsMacOS() ? 62 : 40;

    private static int WouldBlock => OperatingSystem.IsMacOS() ? 35 : 11;

    private const int LockShared = 1;
    private const int LockExclusive = 2;
    private const int LockNoWait = 4;

    // The shim's FileStatus: Flags, then Mode with the kind in its S_IFMT bits, which are POSIX's everywhere. Its whole
    // size is under 128 bytes; the buffer is larger so a field added at its end is never written past.
    private const int StatusBytes = 512;
    private const int KindMask = 0xF000;
    private const int RegularKind = 0x8000;
    private const int FolderKind = 0x4000;

    // `open` is variadic, but only the mode after the flags is, and it is never passed (nothing is created here), so the
    // two fixed arguments are passed as fixed on every ABI, Apple's Arm64 included.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle handle, int operation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle handle, [Out] byte[] status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] status);

    private static (LocalOpenStatus, FileStream?) Ordinary(string reached, FileAccess access, FileShare share)
    {
        try
        {
            return (LocalOpenStatus.Opened, new FileStream(reached, FileMode.Open, access, share));
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            return (LocalOpenStatus.Missing, null);
        }
        catch (IOException)
        {
            return (System.IO.File.Exists(reached) ? LocalOpenStatus.Busy : LocalOpenStatus.Missing, null);
        }
        catch (UnauthorizedAccessException)
        {
            // A folder where a file was asked for is refused this way on Unix; it is no file, as on Windows.
            return (Directory.Exists(reached) ? LocalOpenStatus.Missing : LocalOpenStatus.Unreadable, null);
        }
    }

    // The walk on Windows: OffMachineLinks' rules, held on handles. Each part is opened as itself relative to the folder
    // already open, and a link is followed only by reading its target from the link's own handle. Nothing is ever looked
    // up by a path whose parts could have changed since they were read, so no flip, however timed, makes Windows follow a
    // link to a share.
    private static (LocalOpenStatus Status, SafeFileHandle? Handle, string Reached) Resolve(
        string path, int links, bool directory, bool followLast, uint access, uint share, bool afterJunction = false)
    {
        if (LocalPath.IsOffThisMachine(path) || links > 40)
        {
            return (LocalOpenStatus.OffThisMachine, null, path);
        }

        string full;

        try
        {
            full = OffMachineLinks.FullPath(path);
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (LocalOpenStatus.Unreadable, null, path);
        }

        if (full is not [var letter, ':', '\\', ..] || !char.IsAsciiLetter(letter)
            || full.IndexOf(':', 2) >= 0 || !OffMachineLinks.ReadsAsWritten(full, absolute: true))
        {
            return (LocalOpenStatus.OffThisMachine, null, full);
        }

        var parts = full[3..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        // The letter is read now and the disk opened by that device name, so what the letter stands for cannot change
        // between the two. A subst letter is walked as the folder it stands for, and a letter that is not defined holds
        // nothing, as OffMachineLinks reads one.
        SafeFileHandle? current;

        if (OffMachineLinks.LocalVolumeOf(full[..2]) is { } device)
        {
            LocalOpenStatus opened;

            (opened, current) = Open(null, device + @"\", FolderAccess, ShareAll);

            if (opened != LocalOpenStatus.Opened)
            {
                return (opened, null, full);
            }
        }
        else if (OffMachineLinks.SubstituteOf(full) is { } folder)
        {
            return Resolve(Path.Join(folder, full[2..]), links + 1, directory, followLast, access, share, afterJunction);
        }
        else if (OffMachineLinks.IsUndefinedLetter(full))
        {
            return (LocalOpenStatus.Missing, null, full);
        }
        else
        {
            return (LocalOpenStatus.OffThisMachine, null, full);
        }

        var at = full[..3];

        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var (opened, next) = Open(current, parts[i], last ? access : FolderAccess, last ? share : ShareAll);

            current!.Dispose();

            if (opened != LocalOpenStatus.Opened)
            {
                return (opened, null, Path.Join([at, .. parts[i..]]));
            }

            if (!AttributesOf(next!, out var attributes))
            {
                next!.Dispose();

                return (LocalOpenStatus.Unreadable, null, Path.Join([at, .. parts[i..]]));
            }

            if ((attributes & ReparseAttribute) != 0)
            {
                // A file about to be written is never a link (a launch configuration that is one has always been
                // refused); anything else is followed when its target is on this machine.
                if (last && !followLast)
                {
                    next!.Dispose();

                    return (LocalOpenStatus.Changed, null, Path.Join(at, parts[i]));
                }

                var link = ReadLink(next!);

                next!.Dispose();

                return Follow(link, at, parts[(i + 1)..], afterJunction) is { } target
                    ? Resolve(target, links + 1, directory, followLast, access, share, afterJunction || link is { Junction: true })
                    : (LocalOpenStatus.OffThisMachine, null, Path.Join([at, .. parts[i..]]));
            }

            var isFolder = (attributes & DirectoryAttribute) != 0;

            if (isFolder == (last && !directory))
            {
                next!.Dispose();

                return (LocalOpenStatus.Missing, null, Path.Join([at, .. parts[i..]]));
            }

            // The name the file system keeps, read from the handle: a short spelling (`ZZR15-~1`) is the long one, so what a
            // path reaches compares as the folder it is, as GetFullPath's expansion used to give (review, lens 2 round 15).
            //
            // A name Win32 cannot spell (ending in a dot or a space, reachable only by its short name) is refused, as the
            // walk refuses one typed: tidying would drop that ending, and a link followed from inside it would then be
            // resolved in a different folder of the plain name while Windows resolves it in the real one (review, lens 2
            // round 16).
            var kept = NameOf(next!);

            if (kept is not null && !OffMachineLinks.ReadsAsWritten(kept, absolute: true))
            {
                next!.Dispose();

                return (LocalOpenStatus.OffThisMachine, null, kept);
            }

            at = kept ?? Path.Join(at, parts[i]);
            current = next;
        }

        if (parts.Length == 0 && !directory)
        {
            current!.Dispose();

            return (LocalOpenStatus.Missing, null, at);
        }

        return (LocalOpenStatus.Opened, current, at);
    }

    // Where a link leads, read as OffMachineLinks reads it, joined to the parts after it: null when it leads off this
    // machine or cannot be read. A mount point of a local disk is walked on from that disk's drive letter; one with no
    // letter is refused, since its folder has no name a fresh walk could start from.
    // A relative link reached after a junction is refused: Windows resolves it from the path as it was reached, through
    // the junction, where this walk has the junction's target, and an agent's own `J\R` with `R` -> `..\..\T` was
    // followed here to a decoy while Windows followed it to a share (review, lens 2 round 17).
    private static string? Follow(Link? link, string at, string[] rest, bool afterJunction)
    {
        if (link is not { } found)
        {
            return null;
        }

        string target;

        if (found.Volume is { } volume)
        {
            if (OffMachineLinks.DosDevice(volume) is not { } device || !OffMachineLinks.IsLocalVolume(device)
                || OffMachineLinks.LetteredPath(volume) is not { } lettered)
            {
                return null;
            }

            target = lettered;
        }
        else if (found.Relative)
        {
            if (afterJunction
                || !OffMachineLinks.ReadsAsWritten(found.Target, absolute: false)
                || (Path.IsPathRooted(found.Target) && !Path.IsPathFullyQualified(found.Target)))
            {
                return null;
            }

            target = Path.Combine(at, found.Target);
        }
        else if (found.Target is ['\\', '?', '?', '\\', var drive, ':', '\\', ..] && char.IsAsciiLetter(drive)
                 && OffMachineLinks.ReadsAsWritten(found.Target[4..], absolute: true))
        {
            target = found.Target[4..];
        }
        else
        {
            return null;
        }

        try
        {
            return OffMachineLinks.FullPath(Path.Combine([target, .. rest]));
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // A symbolic link's or mount point's target, read from a handle opened on the link itself. Volume is set for a mount
    // point naming a volume.
    private readonly record struct Link(string Target, bool Relative, string? Volume, bool Junction);

    private static Link? ReadLink(SafeFileHandle handle)
    {
        const uint GetReparsePoint = 0x000900A8;
        const uint SymbolicLink = 0xA000000C;
        const uint MountPoint = 0xA0000003;

        var buffer = new byte[16 * 1024];

        if (!DeviceIoControl(handle, GetReparsePoint, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero)
            || returned < 16)
        {
            return null;
        }

        var tag = BitConverter.ToUInt32(buffer, 0);
        var (start, relative) = tag switch
        {
            SymbolicLink when returned >= 20 => (20, (BitConverter.ToUInt32(buffer, 16) & 1) != 0),
            MountPoint => (16, false),
            _ => (-1, false),
        };

        if (start < 0)
        {
            return null;
        }

        var offset = BitConverter.ToUInt16(buffer, 8);
        var length = BitConverter.ToUInt16(buffer, 10);

        if (start + offset + length > returned)
        {
            return null;
        }

        var target = System.Text.Encoding.Unicode.GetString(buffer, start + offset, length);

        return new Link(target, relative, tag == MountPoint ? OffMachineLinks.MountedVolume(target) : null, tag == MountPoint);
    }

    // An open local file's full name with every part spelled long, from GetFinalPathNameByHandleW: read from the handle and
    // the mount manager, never a lookup by path. Null when it cannot be read or is not a drive-letter path.
    private static string? NameOf(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);

        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var name = new string(buffer, 0, (int)length);

        return name is ['\\', '\\', '?', '\\', var letter, ':', '\\', ..] && char.IsAsciiLetter(letter) ? name[4..] : null;
    }

    private static bool AttributesOf(SafeFileHandle handle, out uint attributes)
    {
        var info = new byte[8];
        var read = GetFileInformationByHandleEx(handle, AttributeTagInfo, info, (uint)info.Length);

        attributes = read ? BitConverter.ToUInt32(info, 0) : 0;

        return read;
    }

    // One part, opened as itself and never through it: FILE_OPEN_REPARSE_POINT on a name with no separator, relative to
    // the folder already open.
    private static (LocalOpenStatus, SafeFileHandle?) Open(
        SafeFileHandle? root, string name, uint access, uint share, uint disposition = OpenExisting)
    {
        var text = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var added = false;

        try
        {
            Marshal.StructureToPtr(
                new UnicodeString
                {
                    Length = checked((ushort)(name.Length * 2)),
                    MaximumLength = checked((ushort)(name.Length * 2)),
                    Buffer = text,
                },
                unicode,
                fDeleteOld: false);

            var rootHandle = IntPtr.Zero;

            if (root is not null)
            {
                root.DangerousAddRef(ref added);
                rootHandle = root.DangerousGetHandle();
            }

            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = rootHandle,
                ObjectName = unicode,
                Attributes = CaseInsensitive,
            };

            var status = (uint)NtCreateFile(
                out var handle, access, ref attributes, out _, IntPtr.Zero, 0, share, disposition,
                SynchronousIo | OpenReparsePoint | (disposition == CreateNew ? NonDirectory : 0), IntPtr.Zero, 0);

            if (status == 0)
            {
                return (LocalOpenStatus.Opened, handle);
            }

            handle.Dispose();

            return (status switch
            {
                NameNotFound or PathNotFound or IsADirectory or NotADirectory => LocalOpenStatus.Missing,
                NameCollision => LocalOpenStatus.Busy,
                SharingViolation => LocalOpenStatus.Busy,
                _ => LocalOpenStatus.Unreadable,
            }, null);
        }
        catch (OverflowException)
        {
            return (LocalOpenStatus.Unreadable, null);
        }
        finally
        {
            if (added)
            {
                root!.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    // `DllImport` for the reason Windowless gives, resolved from System32 only so a planted DLL is never loaded.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle handle,
        uint access,
        ref ObjectAttributes attributes,
        out IoStatusBlock status,
        IntPtr allocationSize,
        uint fileAttributes,
        uint share,
        uint disposition,
        uint options,
        IntPtr extendedAttributes,
        uint extendedAttributesLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, IntPtr input, uint inputSize, [Out] byte[] output, uint outputSize,
        out uint returned, IntPtr overlapped);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, [Out] byte[] information, uint size);
}
