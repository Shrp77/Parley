namespace Parley.Core;

/// <summary>
/// Compares the workspace directories two registrations report, for SPEC.md §4.1.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purely syntactic, and it has to be.</b> The broker performs this comparison, and the broker
/// is not necessarily on the machine whose paths it is comparing — a deployment's whole point is
/// that agents run elsewhere. So nothing here touches a filesystem, resolves a link, or expands a
/// relative path: those answers are only available where the directory is, and a broker that
/// guessed at them from another box would be inventing facts about a machine it cannot see.
/// </para>
/// <para>
/// <b>Separators are unified because they are spelling, not identity.</b> One client reports
/// <c>C:/repo</c> and another <c>C:\repo</c> for the same directory, and comparing those as raw
/// text refuses a repository its own name — with a message telling the operator to retire the
/// other registration, which is the wrong remedy for a path spelled two ways.
/// </para>
/// <para>
/// <b>Case is deliberately significant, and that is the interesting decision.</b> Whether two
/// paths differing only in case name one directory is a property of a filesystem this code
/// cannot inspect: they are the same on NTFS and different on ext4. The comparison must
/// therefore be wrong in one direction, and the two errors are not symmetrical. Treating them as
/// equal admits a second repository under a name that already belongs to one — which is the
/// exact failure §4.1.1 exists to prevent, is silent where it happens, and surfaces much later
/// as a peer receiving replies about a repository it never asked about. Treating them as
/// different refuses a registration that might have been legitimate, which is loud, immediate,
/// and addressed to the person who can fix it.
/// </para>
/// <para>
/// So a difference this code cannot resolve becomes a refusal. That is the same reasoning the
/// collision check itself rests on: it stops a mistake rather than an attacker, and a mistake
/// stopped noisily is worth more than one admitted quietly.
/// </para>
/// </remarks>
public static class WorkspacePath
{
    /// <summary>
    /// Whether two reported workspaces name the same repository.
    /// </summary>
    /// <param name="left">One workspace, as reported.</param>
    /// <param name="right">The other.</param>
    /// <returns>
    /// <see langword="true"/> if they differ only in ways that are spelling rather than identity.
    /// </returns>
    /// <remarks>
    /// Two blanks are not the same repository. A registration that reports no workspace cannot be
    /// checked at all — which the caller handles before reaching here — and answering
    /// <see langword="true"/> for a pair of unknowns would turn "we cannot tell" into "these
    /// match", the one answer the absence of a workspace does not license.
    /// </remarks>
    public static bool SameRepository(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reduces a reported workspace to the form two spellings of it share.
    /// </summary>
    /// <param name="workspace">The workspace, as reported.</param>
    /// <returns>The comparable form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    /// <remarks>
    /// Public because SPEC.md §4.1.1 states this rule rather than leaving it to an
    /// implementation: two brokers comparing differently would admit different registrations, so
    /// the reduction is part of the protocol and not a private detail of this one. An operator
    /// reading a refusal also has a way to see what was actually compared.
    /// </remarks>
    public static string Canonical(string workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var trimmed = workspace.Trim();

        // Separators are unified only where a backslash cannot be part of a name. On Windows it
        // is a separator and `C:/repo` and `C:\repo` are one directory; on a Unix filesystem it
        // is an ordinary character, so `/src/a\b` and `/src/a/b` are two directories and
        // rewriting one into the other would merge two repositories under one name -- the very
        // failure this comparison exists to refuse, reintroduced by the fix for the other half
        // of it.
        var unified = LooksWindows(trimmed) ? trimmed.Replace('\\', '/') : trimmed;

        // A trailing separator is spelling too: a client that appends one is naming the same
        // directory. Not trimmed down to a bare drive, because `C:` and `C:/` differ on Windows
        // -- one is the root and the other is that drive's current directory.
        return unified.Length > 1 && !unified.EndsWith(":/", StringComparison.Ordinal)
            ? unified.TrimEnd('/')
            : unified;
    }

    /// <summary>Whether a path is spelled the way only Windows spells one.</summary>
    /// <param name="path">The trimmed path.</param>
    /// <returns><see langword="true"/> for a drive-qualified or UNC path.</returns>
    /// <remarks>
    /// Deliberately not "does this contain a backslash". A Unix path may legitimately contain
    /// one, and reading it as a separator is what merges two directories; a drive letter or a
    /// UNC prefix is a shape no Unix absolute path has.
    /// </remarks>
    private static bool LooksWindows(string path)
    {
        // A UNC path spelled with backslashes, and not one already spelled with forward slashes:
        // that one needs no conversion to match this, so admitting it here would widen what
        // counts as Windows for no gain -- and `//src/x` is a path a Unix client can report.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        return path.Length >= 2
            && path[1] == ':'
            && char.IsAsciiLetter(path[0]);
    }
}
