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
/// <b>A file that is itself a link is refused, and a folder above it is not</b>: some systems keep ordinary folders behind
/// links (macOS's <c>/var</c>), and through a folder link the file reached still has the name the person typed, which a
/// placement must end in <c>.md</c> or <c>.txt</c>. Every link along the path is still walked for one that leads off this
/// machine (<see cref="OffMachineLinks"/>).
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

        var typed = OffMachineLinks.Typed(path);

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
}
