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

    /// <summary>A path with its drive letter replaced by the folder <c>subst</c> made it stand for, once.</summary>
    /// <param name="full">A full path.</param>
    /// <returns>The path on the folder the letter names, or <paramref name="full"/> when the letter is a drive's own.</returns>
    /// <remarks>QueryDosDevice names a subst letter as <c>\??\C:\folder</c>; reading that name opens nothing.</remarks>
    internal static string Unsubstituted(string full)
    {
        var name = DosDevice(full);

        return name is not null && name.StartsWith(@"\??\", StringComparison.Ordinal) && name.Length > 6 && name[5] == ':'
            ? Path.Join(name[4..], full[2..])
            : full;
    }

    // Whether the drive a full path is on is a network drive, or a letter defined for something that is not a folder on
    // a disk, after following subst letters to the drive they stand on.
    private static bool DriveIsElsewhere(string full)
    {
        var on = full;

        for (var i = 0; ; i++)
        {
            var real = Unsubstituted(on);

            if (string.Equals(real, on, StringComparison.Ordinal))
            {
                break;
            }

            if (i > MostLinks || LocalPath.IsOffThisMachine(real))
            {
                return true;
            }

            on = real;
        }

        try
        {
            var root = Path.GetPathRoot(on) ?? on;

            return new DriveInfo(root).DriveType switch
            {
                DriveType.Network => true,

                // A letter nobody defined is only a folder that is not there; one defined for something with no root
                // folder is a device.
                DriveType.NoRootDirectory => DosDevice(on) is not null,
                _ => false,
            };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // What a drive letter is defined as, or null when it is not a letter or not defined.
    private static string? DosDevice(string full)
    {
        if (!OperatingSystem.IsWindows() || full.Length < 2 || full[1] != ':' || !char.IsAsciiLetter(full[0]))
        {
            return null;
        }

        var target = new char[1024];

        try
        {
            return QueryDosDevice(full[..2], target, (uint)target.Length) == 0 ? null : new string(target).Split('\0')[0];
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

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

        // The walk lands on a drive letter as typed and again after every link. Its kind is asked, which opens nothing,
        // before any part of it is read: a link to a mapped network letter, or to a letter a program defined for a share,
        // would otherwise sign the person in to that host (review, lens 2 run 74). A colon past the drive is a stream
        // spelling, `folder::$INDEX_ALLOCATION`, which is the folder to Windows but not to a comparison of text (lens 1
        // run 70).
        if (OperatingSystem.IsWindows() && (full.IndexOf(':', 2) >= 0 || DriveIsElsewhere(full)))
        {
            return true;
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

            try
            {
                target = new FileInfo(current).LinkTarget;
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            // A reparse point whose target cannot be read is never walked through: its own permissions can hide a link
            // to a share (review, lens 1 run 68). A cloud placeholder is refused with it, as the file location refuses
            // every reparse point. A target rooted without a drive is read against whichever drive the process is on,
            // not the link's, so it is not followed either.
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
