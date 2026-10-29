using Parley.Core;

namespace Parley.Setup;

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
        path is not null && Leads(path.Trim(), 0);

    /// <summary>Whether an entry already found in a folder is a link that leads off this machine.</summary>
    /// <param name="entry">The entry, as enumeration found it.</param>
    /// <returns><see langword="true"/> when nothing inside it may be looked up.</returns>
    public static bool IsLinkOffThisMachine(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && LeadsOffThisMachine(entry.FullName);
    }

    private static bool Leads(string path, int links)
    {
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

        var root = Path.GetPathRoot(full) ?? string.Empty;
        var parts = full[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);

            string? target;

            try
            {
                target = new FileInfo(current).LinkTarget;
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            {
                // A part that cannot be read is not followed here, and the lookup after this fails on it locally.
                return false;
            }

            if (target is null)
            {
                continue;
            }

            var reached = Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current) ?? root, target);

            return Leads(Path.Combine([reached, .. parts[(i + 1)..]]), links + 1);
        }

        return false;
    }
}
