using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That every file on this surface says why it is the way it is, not only what it does.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.6. CLAUDE.md's documentation rule is specific: <c>remarks</c>
/// carries <i>why</i>, never a restatement of the signature, because the reasoning behind a
/// non-obvious choice is the part that cannot be recovered by reading the code.
/// </para>
/// <para>
/// This surface was rewritten wholesale, and a rewrite is where reasons are most easily lost —
/// every one of these files replaced something whose decisions had already been argued once. The
/// check is deliberately shallow: it asserts that each type carries a summary and a remarks block,
/// which a person can then read. Whether the remarks say anything worth reading is not something a
/// test can decide, and pretending otherwise would be worse than not checking.
/// </para>
/// </remarks>
public sealed class DocumentedTests
{
    private static IEnumerable<FileInfo> Sources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return new DirectoryInfo(Path.Combine(directory!.FullName, "src", "Parley.Cli", "Top"))
            .EnumerateFiles("*.cs");
    }

    [Fact]
    public void Every_type_on_this_surface_carries_a_summary()
    {
        // The build already enforces this — CS1591 is an error — so what this adds is the count:
        // a file that lost its type would pass the compiler and fail here.
        foreach (var file in Sources())
        {
            var text = File.ReadAllText(file.FullName);

            Assert.True(
                text.Contains("/// <summary>", StringComparison.Ordinal),
                $"{file.Name} has no summary at all.");
        }
    }

    [Fact]
    public void Every_type_on_this_surface_says_why_it_is_the_way_it_is()
    {
        // The rule the compiler cannot enforce. A file with no remarks is a file whose decisions
        // were either obvious or lost, and after a rewrite the second is the likely one.
        var silent = Sources()
            .Where(file => !File.ReadAllText(file.FullName)
                .Contains("/// <remarks>", StringComparison.Ordinal))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            silent.Count == 0,
            "These say what they do and not why: " + string.Join(", ", silent));
    }

    [Fact]
    public void The_reasoning_is_more_than_a_sentence_where_the_choice_was_a_real_one()
    {
        // Not a length rule for its own sake. Each of these replaced something whose decisions had
        // been argued once already, and each carries a decision this feature made rather than
        // inherited -- the layout, the escaping, the two files, the three operations.
        foreach (var name in new[]
                 {
                     "Shell.cs", "Safe.cs", "Adding.cs", "Withdrawing.cs", "Prompting.cs",
                 })
        {
            var text = File.ReadAllText(Sources().Single(file => file.Name == name).FullName);
            var remarks = Regex.Matches(text, "/// <para>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

            Assert.True(
                remarks.Count >= 2,
                $"{name} carries {remarks.Count} paragraph(s) of reasoning for a file that made "
                + "more than one decision.");
        }
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        var sources = Sources().ToList();

        Assert.NotEmpty(sources);
        Assert.Contains(sources, file => file.Name == "Withdrawing.cs");
    }
}
