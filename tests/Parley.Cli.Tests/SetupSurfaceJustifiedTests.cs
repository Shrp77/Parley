using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That every public type in <c>Parley.Setup</c> is public because something ships it.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 2. <c>SetupSurfaceTests</c> enumerates the public types and fails when the
/// list changes, which makes adding one a decision — and over this gate that decision was made four
/// times by adding a name to a list, with no reason attached and nothing asking for one. An
/// enumeration with no justification becomes a rubber stamp, which is the failure mode of every
/// checklist.
/// </para>
/// <para>
/// This asks the question the list cannot: is the type reachable from either tool? <c>Parley.Setup</c>
/// is referenced by <c>parley</c> and by <c>parley-terminal</c> and by nothing else, so a public type
/// neither of them names is public for the tests' benefit — and the tests can be granted internals
/// instead, which is what the rest of this repository does.
/// </para>
/// </remarks>
public sealed class SetupSurfaceJustifiedTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The source of both tools, as one string.</summary>
    private static string Tools()
    {
        var text = new List<string>();

        foreach (var project in (string[])["Parley.Cli", "Parley.Terminal"])
        {
            foreach (var file in Directory.EnumerateFiles(
                Path.Combine(Root, "src", project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                text.Add(File.ReadAllText(file));
            }
        }

        return string.Join("\n", text);
    }

    [Fact]
    public void Every_public_type_is_named_by_a_tool()
    {
        var tools = Tools();

        // The control first: without it a walk that found no sources would pass this whole suite.
        Assert.Contains("Enrolment", tools, StringComparison.Ordinal);

        // **Named exemptions with reasons, which is the whole complaint about the list this
        // replaces.** These five predate the multi-project work and are the enrolment manifest's own
        // shapes — `EnrollmentManifest` is the file `enroll scan` writes and `enroll apply` reads,
        // and the other four are the picker's. They are reachable only through methods a tool does
        // call, so making them internal is a cascade through public signatures rather than a flip,
        // and doing it inside a feature about launch keys would be a diff nobody asked for. Recorded
        // in REVIEW-LEDGER.md as low, and left for whoever touches that file next.
        var predating = new HashSet<string>(StringComparer.Ordinal)
        {
            "EnrollmentManifest",
            "RepositoryPlan",
            "RepositorySelection",
            "SelectableRepository",
            "SelectionStatus",
        };

        var unused = typeof(Enrolment).Assembly
            .GetExportedTypes()
            .Where(type => !type.IsNested)
            .Select(type => type.Name)
            .Where(name => !tools.Contains(name, StringComparison.Ordinal) && !predating.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unused.Length == 0,
            "public in Parley.Setup and named by neither tool: " + string.Join(", ", unused)
            + ". Make them internal and grant the test projects InternalsVisibleTo, which is what "
            + "the rest of this repository does, or say here why the surface is wider than its use.");
    }
}
