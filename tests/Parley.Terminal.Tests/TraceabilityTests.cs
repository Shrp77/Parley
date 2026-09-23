using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That every requirement the specification numbers is answered by something a reader can find.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.9. The specification numbers nineteen requirements and the
/// tasks list claims each is done. Nothing checked that the claim leads anywhere: a requirement met
/// by code nobody can locate from its number is met the way an undocumented decision is made — the
/// next person re-derives it, or changes it without knowing what it was for.
/// </para>
/// <para>
/// Deliberately a weak check made honestly rather than a strong one made up. It asserts the number
/// is written down beside something, not that the something is correct. Whether a requirement is
/// actually satisfied is what the rest of the suite is for, and a traceability test that claimed
/// more would be the worst kind: green, and about nothing.
/// </para>
/// </remarks>
public sealed class TraceabilityTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "specs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static string Feature(string file) =>
        File.ReadAllText(Path.Combine(Root(), "specs", "005-tabbed-terminal-agents", file));

    /// <summary>Every requirement number the specification defines.</summary>
    private static IReadOnlyList<string> Required() =>
        [.. Regex.Matches(Feature("spec.md"), @"^- \*\*(FR-\d+)", RegexOptions.Multiline, TimeSpan.FromSeconds(2))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>Everything written in the source and the tests of this feature.</summary>
    private static string Written()
    {
        var said = new List<string>();

        foreach (var tree in new[]
                 {
                     Path.Combine(Root(), "src", "Parley.Terminal"),
                     Path.Combine(Root(), "tests", "Parley.Terminal.Tests"),
                 })
        {
            foreach (var file in Directory.EnumerateFiles(tree, "*.cs", SearchOption.AllDirectories))
            {
                if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    said.Add(File.ReadAllText(file));
                }
            }
        }

        return string.Join("\n", said);
    }

    [Fact]
    public void Every_numbered_requirement_is_named_somewhere_a_reader_can_reach()
    {
        var written = Written() + Feature("contracts/operations.md") + Feature("contracts/surface.md");

        var orphaned = Required()
            .Where(number => !written.Contains(number, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            "Nothing in the code, the tests or the contracts names these: "
            + string.Join(", ", orphaned));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // The trap this suite has hit before: a path that stops resolving makes every case pass.
        Assert.Equal(19, Required().Count);
        Assert.Contains("FR-001", Required());
        Assert.Contains("Shell", Written(), StringComparison.Ordinal);
    }
}
