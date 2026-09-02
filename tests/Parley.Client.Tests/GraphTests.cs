using System.Xml.Linq;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a client stays a client: no route to a store, by any number of hops.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4 gives the database to one process, and the console's own guard has said so since it
/// was built. What that guard reads is the console's <i>direct</i> references, which was a
/// complete check for exactly as long as the console referenced nothing but Configuration and
/// Transport.
/// </para>
/// <para>
/// <b>The move ended that, and the guard did not notice.</b> The console now references
/// Parley.Client, so a store acquired <i>here</i> would reach the console transitively and its
/// one-hop check would still pass — a guard that keeps reporting success while the thing it
/// guards has become reachable. Which is why the walk below is transitive, and why the console's
/// own check was made transitive in the same change rather than left reading one hop of a graph
/// that is now two deep.
/// </para>
/// <para>
/// Read as references rather than as text, for the reason the console's version already records:
/// these project files say in prose that there is no SQLite here and never may be, and a check
/// that matched any occurrence of the word would fail on the sentence promising the thing it is
/// checking for.
/// </para>
/// </remarks>
public sealed class GraphTests
{
    /// <summary>What a client must never be able to reach, at any depth.</summary>
    private static readonly string[] Forbidden = ["Sqlite", "Parley.Store", "Parley.Broker"];

    [Fact]
    public void The_client_references_exactly_what_the_console_referenced_before_the_move()
    {
        // The whole argument for the extraction was that nothing landed on anybody's graph that
        // was not already there. That is only true while this list is these two, so it is
        // asserted rather than described: an added reference here is an addition to the console's
        // graph too, and to the terminal's, made in a project neither of them is named in.
        var referenced = DirectReferences(RepositoryPath("src/Parley.Client/Parley.Client.csproj"))
            .Where(r => r.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Parley.Configuration", "Parley.Transport"], referenced);
    }

    [Theory]
    [InlineData("src/Parley.Client/Parley.Client.csproj")]
    [InlineData("src/Parley.Web/Parley.Web.csproj")]
    public void Nothing_reachable_from_a_client_can_open_a_store(string project)
    {
        // Both, and transitively. The console is included here as well as in its own suite
        // because this is the assembly that would introduce the breach: a check living only
        // beside the console would be a check nobody edits when the client changes.
        var reached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(RepositoryPath(project), project, reached);

        var breaches = reached
            .Where(r => Forbidden.Any(f => r.Key.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(r => $"{r.Key} (via {r.Value})")
            .ToList();

        Assert.True(
            breaches.Count == 0,
            $"{project} has acquired a route to a store: {string.Join(", ", breaches)}");
    }

    /// <summary>Follows project references from one project file, depth first.</summary>
    /// <param name="path">The project file to read.</param>
    /// <param name="via">How this project was reached, for the failure message.</param>
    /// <param name="reached">Everything found so far, by name, with the path that led to it.</param>
    /// <remarks>
    /// Package references are recorded but not followed: a package's own dependencies are not in
    /// this repository, and the thing being guarded against — Microsoft.Data.Sqlite — is a direct
    /// reference wherever it appears here.
    /// </remarks>
    private static void Walk(string path, string via, Dictionary<string, string> reached)
    {
        foreach (var reference in DirectReferences(path))
        {
            var name = Path.GetFileNameWithoutExtension(reference);

            if (!reached.TryAdd(name, via))
            {
                continue;
            }

            if (!reference.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(path);
            Assert.NotNull(directory);

            var next = Path.GetFullPath(Path.Combine(directory!, reference.Replace('\\', '/')));

            if (File.Exists(next))
            {
                Walk(next, name, reached);
            }
        }
    }

    /// <summary>The package and project references a project file declares itself.</summary>
    /// <param name="path">The project file.</param>
    /// <returns>The value of every <c>Include</c> attribute on a reference element.</returns>
    private static List<string> DirectReferences(string path) =>
        [.. XDocument.Load(path).Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(v => v.Length > 0)];

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
