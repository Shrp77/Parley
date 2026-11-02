using Parley.Core;

namespace Parley.Configuration;

/// <summary>Whether a file the person names may be read to place it in a conversation (SPEC.md §9.5).</summary>
/// <remarks>
/// <para>
/// <b>Asked before the path is looked up at all</b> (lens 2 run 122). An agent's own placing tool refuses a path through a
/// link, so an agent that wanted a file outside its repository placed could make a link in its repository and ask the
/// person to place it: the person's placement read the link's target, a key or another repository's file, and offered it
/// to every agent in the project under the person's name. A path to another machine, typed or behind a link, made Windows
/// sign in to that host before anything was read.
/// </para>
/// <para>
/// <b>A file that is itself a link is refused, and a folder link above it or a hard link is not</b>: some systems keep
/// ordinary folders behind links (macOS's <c>/var</c>), and the owner ruled (lens 2 run 123) that a link an agent makes in
/// its own repository gives it nothing it could not read and send itself. What stays refused is a lookup that leaves this
/// machine: every link along the path is walked for one that does (<see cref="OffMachineLinks"/>).
/// </para>
/// </remarks>
public static class PlaceableFile
{
    /// <summary>Why a file must not be read to place it, or <see langword="null"/> when it may.</summary>
    /// <param name="path">The file, as the person wrote it.</param>
    /// <returns>One sentence saying what is wrong, or <see langword="null"/>.</returns>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var typed = Typed(path);

        if (LocalPath.IsOffThisMachine(typed) || OffMachineLinks.LeadsOffThisMachine(typed))
        {
            return $"{typed} is on another machine, or reached through a link to one, and Parley does not look up a path "
                + "there. Copy the file to this machine and place the copy.";
        }

        FileInfo file;

        try
        {
            file = new FileInfo(typed);
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? $"{typed} is a link to another file. Place the file itself, not a link to it."
            : null;
    }

    /// <summary>A path as a person typed it, with only the blanks typing adds taken off its ends: what is checked.</summary>
    /// <param name="path">The path as written.</param>
    /// <returns>The path to check and then read.</returns>
    /// <remarks>
    /// <b>The caller reads this string, not the one it was given</b> (lens 1 run 119): checked trimmed and read untrimmed,
    /// a link named with a leading or trailing blank beside a file without one was checked as the one and read as the
    /// other.
    /// </remarks>
    public static string Typed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return OffMachineLinks.Typed(path);
    }
}
