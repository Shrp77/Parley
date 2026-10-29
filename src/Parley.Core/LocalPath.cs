namespace Parley.Core;

/// <summary>
/// Whether a path a person or an agent wrote names somewhere off this machine, decided from the text alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decided before anything touches the disk, because touching it is the harm.</b> On Windows, asking whether
/// <c>\\host\share</c> exists opens a session to that host and offers the signed-in person's credentials, which can be
/// cracked or relayed. An agent's message renders as live links, so a link into a page that looks a path up would have
/// the person's own click make that offer to a host the agent named (review, lens 2 run 66 onward).
/// </para>
/// <para>
/// Every spelling of a network or device path starts with two separators, in either direction, or with the
/// <c>\??\</c> prefix. A path that only begins like one on a system where it means a local root is refused too: Parley
/// asks for a folder on this machine, and refusing there costs nothing.
/// </para>
/// </remarks>
public static class LocalPath
{
    /// <summary>What a refusal says.</summary>
    public const string OffThisMachine =
        "That path names another machine or a device, so nothing was looked up. Name a folder on this machine.";

    /// <summary>Whether a path names a network share, another machine or a device.</summary>
    /// <param name="path">The path as written.</param>
    /// <returns><see langword="true"/> when it must not be looked up.</returns>
    public static bool IsOffThisMachine(string? path)
    {
        var written = path?.TrimStart() ?? string.Empty;

        return (written.Length >= 2 && written[0] is '\\' or '/' && written[1] is '\\' or '/')
            || written.StartsWith(@"\??\", StringComparison.Ordinal);
    }
}
