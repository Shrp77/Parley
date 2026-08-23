namespace Parley.Mcp.Tests;

/// <summary>
/// Reads a file from the repository root, for the checks that measure code against prose.
/// </summary>
/// <remarks>
/// <para>
/// Shared because there were two copies within a day of each other — one for SPEC.md and one for
/// README.md — and the thing they duplicate is the part that is easy to get wrong. Both are
/// checked out with CRLF on Windows and LF on the Linux and macOS runners, and .NET anchors
/// <c>$</c> in multiline mode before a bare line feed only. A copy that forgot to normalise
/// would make every pattern anchored to end-of-line match nothing on one platform of three, and
/// a check that passes on two runners is worse than no check.
/// </para>
/// <para>
/// The walk upwards rather than a relative path is the other half: the test host's working
/// directory differs between <c>dotnet test</c>, an IDE runner, and CI, so the only stable
/// anchor is the assembly's own location.
/// </para>
/// </remarks>
internal static class RepositoryFile
{
    /// <summary>Reads a file from the first directory above the test assembly that holds it.</summary>
    /// <param name="name">The file name, as it appears at the repository root.</param>
    /// <returns>Its text, with line endings normalised to line feeds.</returns>
    /// <exception cref="InvalidOperationException">No directory above the assembly holds it.</exception>
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(RootHolding(name), name))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Finds the directory above the test assembly that holds a file.</summary>
    /// <param name="name">The file name, as it appears at the repository root.</param>
    /// <returns>That directory's full path.</returns>
    /// <exception cref="InvalidOperationException">No directory above the assembly holds it.</exception>
    /// <remarks>
    /// Separated from <see cref="Read"/> because a second kind of check needs the location rather
    /// than the contents: the plugin's marketplace resolves a relative source path from beside
    /// itself, so verifying it means resolving that path the way an installing machine would.
    /// Kept here rather than copied there, which is the mistake this whole class exists because
    /// of — there were two copies of the walk within a day of each other, and the half that is
    /// easy to get wrong is not the loop.
    /// </remarks>
    public static string RootHolding(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, name)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"{name} not found in any directory above {AppContext.BaseDirectory}.");
    }
}
