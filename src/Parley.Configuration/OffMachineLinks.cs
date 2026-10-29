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
