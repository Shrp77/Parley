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

            return Ordinary(reached, access, share);
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
    /// rather than answered as no text, which would read as no file (a plan saying <i>create</i> for one that exists).</exception>
    public static (LocalOpenStatus Status, string? Text) ReadText(string path, bool json)
    {
        var (status, stream) = File(path, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, followLink: true);

        using (stream)
        {
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

    /// <summary>Whether a file or a folder that must be on this machine is there, following links to things here.</summary>
    /// <param name="path">The path.</param>
    /// <returns><see cref="LocalOpenStatus.Opened"/> when something is there.</returns>
    public static LocalOpenStatus Entry(string path)
    {
        // Something there that this account cannot open, or another holds, is still there: a `.git` folder nobody may list
        // is a repository all the same, as Directory.Exists said.
        static LocalOpenStatus There(LocalOpenStatus status) =>
            status is LocalOpenStatus.Busy or LocalOpenStatus.Unreadable ? LocalOpenStatus.Opened : status;

        var folder = There(Folder(path));

        if (folder != LocalOpenStatus.Missing)
        {
            return folder;
        }

        var (status, stream) = File(path, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, followLink: true);

        stream?.Dispose();

        return There(status);
    }

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
        string path, int links, bool directory, bool followLast, uint access, uint share)
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
            return Resolve(Path.Join(folder, full[2..]), links + 1, directory, followLast, access, share);
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

                return Follow(link, at, parts[(i + 1)..]) is { } target
                    ? Resolve(target, links + 1, directory, followLast, access, share)
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
            at = NameOf(next!) ?? Path.Join(at, parts[i]);
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
    private static string? Follow(Link? link, string at, string[] rest)
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
            if (!OffMachineLinks.ReadsAsWritten(found.Target, absolute: false)
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
    private readonly record struct Link(string Target, bool Relative, string? Volume);

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

        return new Link(target, relative, tag == MountPoint ? OffMachineLinks.MountedVolume(target) : null);
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
    private static (LocalOpenStatus, SafeFileHandle?) Open(SafeFileHandle? root, string name, uint access, uint share)
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
                out var handle, access, ref attributes, out _, IntPtr.Zero, 0, share, OpenExisting,
                SynchronousIo | OpenReparsePoint, IntPtr.Zero, 0);

            if (status == 0)
            {
                return (LocalOpenStatus.Opened, handle);
            }

            handle.Dispose();

            return (status switch
            {
                NameNotFound or PathNotFound or IsADirectory or NotADirectory => LocalOpenStatus.Missing,
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
