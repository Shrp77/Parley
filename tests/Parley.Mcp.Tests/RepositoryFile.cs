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
    public static string Read(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, name);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"{name} not found in any directory above {AppContext.BaseDirectory}.");
    }
}
