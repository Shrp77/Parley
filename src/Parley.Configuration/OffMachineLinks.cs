using Parley.Core;

namespace Parley.Configuration;

/// <summary>
/// Whether a path leads off this machine once its links are followed, found by reading link targets and never by
/// opening what they point at.
/// </summary>
/// <remarks>
/// <para>
/// <b>The text of a path is not enough.</b> <see cref="LocalPath"/> refuses a path written as a share, but a folder in a
/// repository can be a link to <c>\\host\share</c>: the path reads as local, and looking it up follows the link and
/// opens a session to that host with the person's sign-in (review, lens 1 run 67). An agent can make such a link in its
/// own repository, and a clone can carry one.
/// </para>
/// <para>
/// So every part of the path is asked what it links to, which reads the link itself on this machine, and the walk
/// continues from the target. Nothing here calls <c>Exists</c> on a path whose parts have not been read this way.
/// </para>
/// <para>
/// <b>On Windows a drive letter is judged by its name.</b> Each letter the walk lands on is read with QueryDosDevice,
/// never asked what kind of drive it is, because asking can open a share: a local disk volume is walked, a
/// <c>subst</c> letter is walked as the folder it stands for, a letter that is not defined holds nothing to read, and
/// any other letter is refused. A mount point is walked through only when its volume is a local disk.
/// </para>
/// </remarks>
public static class OffMachineLinks
{
    private const int MostLinks = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>Whether a path, or any link along it, leads off this machine.</summary>
    /// <param name="path">The path as written.</param>
    /// <returns><see langword="true"/> when it must not be looked up.</returns>
    public static bool LeadsOffThisMachine(string? path) =>
        path is not null && Leads(path.Trim(), 0, 0, out _);

    /// <summary>Where a path leads once every link along it is followed, unless it leads off this machine.</summary>
    /// <param name="path">The path as written.</param>
    /// <param name="reached">The path the walk ends on, links resolved; the path as written when it could not be read.</param>
    /// <returns><see langword="true"/> when it must not be looked up, as <see cref="LeadsOffThisMachine(string?)"/>.</returns>
    /// <remarks>
    /// What a folder is on is decided by where it really is: a junction from <c>C:\code</c> into a synced folder reads
    /// as local and is synced all the same (review, lens 1 run 69).
    /// </remarks>
    internal static bool LeadsOffThisMachine(string path, out string reached) => Leads(path.Trim(), 0, 0, out reached);

    // What a drive letter is, decided from the name Windows keeps for it and never by asking the drive: asking a letter
    // defined as `\??\GLOBALROOT\Device\Mup\host\share` what kind of drive it is opens the share (review, lens 2 run 75).
    // A local disk's letter names a `\Device\HarddiskVolume<n>`; a subst letter names `\??\C:\folder`; anything else, a
    // share, a redirector, WebDAV, a FUSE driver's own device, is not this machine's own disk.
    private enum LetterKind
    {
        NotALetter,
        Undefined,
        LocalDisk,
        Substitute,
        Elsewhere,
    }

    private static (LetterKind Kind, string Folder) Letter(string full)
    {
        if (full.Length < 2 || full[1] != ':' || !char.IsAsciiLetter(full[0]))
        {
            return (LetterKind.NotALetter, string.Empty);
        }

        return DosDevice(full[..2]) switch
        {
            null => (LetterKind.Undefined, string.Empty),
            ['\\', '?', '?', '\\', _, ':', ..] name when char.IsAsciiLetter(name[4]) => (LetterKind.Substitute, name[4..]),
            var name when IsLocalVolume(name) => (LetterKind.LocalDisk, string.Empty),
            _ => (LetterKind.Elsewhere, string.Empty),
        };
    }

    /// <summary>Whether a full path's drive letter is defined for a share, read from its name alone.</summary>
    /// <param name="full">A full path.</param>
    /// <returns><see langword="true"/> for a mapped drive or a letter a program defined for a share.</returns>
    /// <remarks>For the refusal's words only: the walk refuses such a letter whatever it is called.</remarks>
    internal static bool IsNetworkLetter(string full) =>
        Letter(full) is (LetterKind.Elsewhere, _)
        && DosDevice(full[..2]) is { } name
        && ((string[])[@"\Device\LanmanRedirector", @"\Device\Mup", @"\Device\WebDavRedirector", @"\??\UNC\", @"\??\GLOBALROOT\Device\Mup"])
            .Any(share => name.StartsWith(share, StringComparison.OrdinalIgnoreCase));

    private static bool IsLocalVolume(string device)
    {
        const string Volume = @"\Device\HarddiskVolume";

        return device.Length > Volume.Length
            && device.StartsWith(Volume, StringComparison.Ordinal)
            && device[Volume.Length..].All(char.IsAsciiDigit);
    }

    // A mount point's target, `Volume{GUID}\` as .NET hands it back (it reads as relative), or with `\\?\` or `\??\`.
    private static string? MountedVolume(string target)
    {
        var name = target.StartsWith(@"\\?\", StringComparison.Ordinal) || target.StartsWith(@"\??\", StringComparison.Ordinal)
            ? target[4..]
            : target;
        name = name.TrimEnd('\\');

        return name.Length == "Volume{}".Length + 36
            && name.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)
            && name[^1] == '}'
            && Guid.TryParse(name[6..], out _)
                ? name
                : null;
    }

    // What a DOS device name (`K:`, `Volume{GUID}`) is defined as: null only when Windows says it is not defined, and the
    // empty string, which matches no kind and is refused, when the name could not be read. A definition longer than the
    // buffer failed as "not defined" and the letter was then read (review, lens 2 run 76), so it is asked again with room
    // for the longest a definition can be, and any other failure is not taken for "not defined".
    private static string? DosDevice(string device)
    {
        const int NotFound = 2;
        const int TooSmall = 122;

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var size in (int[])[1024, 65536])
        {
            var target = new char[size];

            try
            {
                if (QueryDosDevice(device, target, (uint)target.Length) != 0)
                {
                    return new string(target).Split('\0')[0];
                }
            }
            catch (EntryPointNotFoundException)
            {
                return string.Empty;
            }

            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

            if (error == NotFound)
            {
                return null;
            }

            if (error != TooSmall)
            {
                break;
            }
        }

        return string.Empty;
    }

    // A symbolic link or a mount point as it is stored: the target exactly as written (`\??\C:\folder`, `\??\Volume{GUID}\`,
    // `\??\UNC\host\share`, or a relative path) and whether a symbolic link's target is relative to the link's folder.
    // A symbolic link whose target is the text `Volume{GUID}` is relative, and its sibling of that name can lead anywhere
    // (lens 1 run 72); only an absolute target names a volume.
    private readonly record struct Link(string Target, bool Relative);

    // FSCTL_GET_REPARSE_POINT on a handle opened on the link itself (FILE_FLAG_OPEN_REPARSE_POINT), which follows nothing.
    // Null when it cannot be read or is another kind of reparse point.
    private static Link? ReadLink(string path)
    {
        const uint ReadAttributes = 0x80;
        const uint ShareAll = 0x7;
        const uint OpenExisting = 3;
        const uint OpenReparsePoint = 0x00200000;
        const uint BackupSemantics = 0x02000000;
        const uint GetReparsePoint = 0x000900A8;
        const uint SymbolicLink = 0xA000000C;
        const uint MountPoint = 0xA0000003;
        const uint RelativeFlag = 0x1;

        using var handle = CreateFile(
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path,
            ReadAttributes,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint | BackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[16 * 1024];

        if (!DeviceIoControl(handle, GetReparsePoint, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero)
            || returned < 16)
        {
            return null;
        }

        // REPARSE_DATA_BUFFER: the tag, then the substitute name's offset and length into the path buffer, which starts
        // after the flags for a symbolic link and straight after the four offsets for a mount point.
        var tag = BitConverter.ToUInt32(buffer, 0);
        var (start, relative) = tag switch
        {
            SymbolicLink when returned >= 20 => (20, (BitConverter.ToUInt32(buffer, 16) & RelativeFlag) != 0),
            MountPoint => (16, false),
            _ => (-1, false),
        };

        if (start < 0)
        {
            return null;
        }

        var offset = BitConverter.ToUInt16(buffer, 8);
        var length = BitConverter.ToUInt16(buffer, 10);

        return start + offset + length > returned
            ? null
            : new Link(System.Text.Encoding.Unicode.GetString(buffer, start + offset, length), relative);
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName,
        uint access,
        uint share,
        IntPtr security,
        uint creation,
        uint flags,
        IntPtr template);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device,
        uint code,
        IntPtr input,
        uint inputSize,
        [System.Runtime.InteropServices.Out] byte[] output,
        uint outputSize,
        out uint returned,
        IntPtr overlapped);

    // `DllImport` for the reason Windowless gives, resolved from System32 only so a planted kernel32.dll is never loaded.
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "QueryDosDeviceW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, [System.Runtime.InteropServices.Out] char[] targetPath, uint max);

    // The walk. `fromTarget` is how many leading parts of the path came from the link just followed: a part among those
    // that is not there means the link named something this walk cannot see (an NT path .NET hands back as a relative one,
    // such as GLOBALROOT\Device\Mup\host), so it is refused; a part past them that is not there is only a missing file.
    private static bool Leads(string path, int links, int fromTarget, out string reached)
    {
        reached = path;

        if (LocalPath.IsOffThisMachine(path))
        {
            return true;
        }

        if (links > MostLinks)
        {
            // A chain this long is a loop or a maze; neither is a folder anybody means.
            return true;
        }

        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        reached = full;

        // The walk lands on a drive letter as typed and again after every link, and decides what the letter is before
        // any part of it is read: a link to a mapped network letter, or to a letter a program defined for a share, would
        // otherwise sign the person in to that host (review, lens 2 run 74). A subst letter is walked as the folder it
        // stands for, part by part, since reading through the letter follows whatever that folder links to (run 75). A
        // colon past the drive is a stream spelling, `folder::$INDEX_ALLOCATION`, which is the folder to Windows but not
        // to a comparison of text (lens 1 run 70).
        if (OperatingSystem.IsWindows())
        {
            if (full.IndexOf(':', 2) >= 0)
            {
                return true;
            }

            switch (Letter(full))
            {
                case (LetterKind.Elsewhere, _):
                    return true;

                case (LetterKind.Substitute, var folder):
                    var folderParts = folder[(Path.GetPathRoot(folder) ?? string.Empty).Length..]
                        .Split(Separators, StringSplitOptions.RemoveEmptyEntries).Length;

                    return Leads(Path.Join(folder, full[2..]), links + 1, folderParts + fromTarget, out reached);
            }
        }

        var root = Path.GetPathRoot(full) ?? string.Empty;
        var parts = full[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);

            // Every part before this one is a plain folder, read or followed already, so asking this part's own
            // attributes traverses nothing that has not been checked.
            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
            {
                return i < fromTarget;
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            string? target;

            if (OperatingSystem.IsWindows())
            {
                // Read from the link itself, never from LinkTarget's text: .NET hands an absolute NT target such as
                // `\??\GLOBALROOT\Device\Mup\host\share` back without its prefix, so it read as relative, and a decoy
                // folder of that name beside the link made it look local while Windows followed the real target to
                // the share (review, lens 2 run 77). A reparse point that cannot be read, or is neither a symbolic link
                // nor a mount point (a cloud placeholder among them), is never walked through: its own permissions can
                // hide a link to a share (lens 1 run 68).
                if (ReadLink(current) is not { } link)
                {
                    return true;
                }

                if (link.Relative)
                {
                    target = link.Target;
                }
                else if (link.Target is ['\\', '?', '?', '\\', var drive, ':', '\\', ..] && char.IsAsciiLetter(drive))
                {
                    target = link.Target[4..];
                }
                else if (MountedVolume(link.Target) is { } volume)
                {
                    // A folder that mounts a disk names its volume (a second disk at C:\mnt\data). A local disk's volume
                    // is walked through as a folder; any other volume is not this machine's own (lens 1 run 71).
                    if (DosDevice(volume) is { } device && IsLocalVolume(device))
                    {
                        continue;
                    }

                    return true;
                }
                else
                {
                    // A share, GLOBALROOT, a volume path with folders after it, a device: none is a folder on this
                    // machine's own disk by a path the walk can read.
                    return true;
                }
            }
            else
            {
                try
                {
                    target = new FileInfo(current).LinkTarget;
                }
                catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }

            // A target rooted without a drive is read against whichever drive the process is on, not the link's, so it
            // is not followed either.
            if (target is null
                || (OperatingSystem.IsWindows() && Path.IsPathRooted(target) && !Path.IsPathFullyQualified(target)))
            {
                return true;
            }

            var reachedTarget = Path.GetFullPath(
                Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current) ?? root, target));
            var reachedParts = reachedTarget[(Path.GetPathRoot(reachedTarget) ?? string.Empty).Length..]
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries).Length;

            return Leads(Path.Combine([reachedTarget, .. parts[(i + 1)..]]), links + 1, reachedParts, out reached);
        }

        return false;
    }
}
