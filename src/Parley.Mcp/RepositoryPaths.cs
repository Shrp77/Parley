namespace Parley.Mcp;

/// <summary>
/// Where an agent's file tools may read and write: its own repository, and nowhere else (SPEC.md principle 2.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter is the only process on the agent's machine that touches a placed or fetched file</b>, so it is the
/// only place this can be enforced. The broker never sees the agent's disk, and a path a model supplied is a path
/// a peer's message may have suggested: "fetch this to ../other-repository/config.md" is exactly the reach past an
/// agent's own repository that §13 says a message cannot make.
/// </para>
/// <para>
/// <b>The root is the launch's workspace, else the directory the server was started in</b>, which is the repository a
/// client starts its servers in. Paths are resolved fully before they are compared, and a path that passes through
/// a symbolic link or junction is refused outright rather than resolved: a link inside the repository that points
/// out of it would otherwise satisfy a textual check while writing somewhere else entirely.
/// </para>
/// </remarks>
public static class RepositoryPaths
{
    /// <summary>The repository root for a launch.</summary>
    /// <param name="workspace">The launch's workspace, if it named one.</param>
    /// <returns>The absolute root.</returns>
    public static string Root(string? workspace) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(workspace)
            ? Directory.GetCurrentDirectory()
            : workspace));

    /// <summary>Resolves a path inside the repository, or says why it is not one.</summary>
    /// <param name="root">The repository root, from <see cref="Root"/>.</param>
    /// <param name="candidate">The path the agent gave, relative to the root or absolute.</param>
    /// <param name="resolved">The absolute path, when it is inside the repository.</param>
    /// <returns><see langword="null"/> when the path is usable; otherwise the reason, in words.</returns>
    public static string? Resolve(string root, string? candidate, out string resolved)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "A path is needed.";
        }

        string full;

        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(root, candidate));
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{candidate}' is not a usable path.";
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // The repository itself is inside it: "." names the root, where a fetch keeps the file's name.
        if (string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(root), comparison))
        {
            resolved = root;
            return null;
        }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            return $"'{candidate}' is outside your repository. You read and write files only in your own repository; " +
                   "ask the agent that owns another repository to do anything there.";
        }

        // A part ending in a space or a dot survives GetFullPath in the middle of a path, but asking it alone asks the
        // name without it, so the link check below looked at a missing `docs` while the write went through the real
        // `docs ` link (review, lens 2 run 80). Windows keeps any such part as written, so it is refused.
        if (OperatingSystem.IsWindows()
            && full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part[^1] == '.' || char.IsWhiteSpace(part[^1])))
        {
            return $"'{candidate}' has a folder whose name ends in a space or a dot, which Windows reads differently " +
                   "from how it is written, so it is refused.";
        }

        for (var at = full; at.Length > root.Length; at = Path.GetDirectoryName(at)!)
        {
            FileSystemInfo entry = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);

            if (entry.Exists && entry.LinkTarget is not null)
            {
                return $"'{candidate}' passes through a link, which could lead outside your repository, so it is refused.";
            }
        }

        resolved = full;
        return null;
    }
}
