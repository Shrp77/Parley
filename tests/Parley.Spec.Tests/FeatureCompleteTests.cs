using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// That 006's record says what was built, what was not, and why.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.9 — the last of them. Run 3.3 checked that every requirement is
/// cited somewhere; this checks the harder thing, which is that the record is honest about the
/// feature's edges: the task left open, the decision reversed, and the diagnostic still in place.
/// </para>
/// <para>
/// <b>These are the entries a record loses first.</b> What was built writes itself, because
/// somebody had to do it. What was deliberately not done, and why, survives only if it is written
/// down at the moment — and it is the half the next person actually needs.
/// </para>
/// </remarks>
public sealed class FeatureCompleteTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static string Read(params string[] under) =>
        File.ReadAllText(Path.Combine([Root(), .. under]));

    private static string Tasks() => Read("specs", "006-three-view-operator", "tasks.md");

    /// <summary>Runs lines together, so a wrapped phrase is found by meaning rather than layout.</summary>
    private static string Flowed(string text) =>
        Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    [Fact]
    public void Every_task_is_either_done_or_says_why_it_is_not()
    {
        // An unticked box with no explanation is the entry that reads, months later, as work
        // somebody forgot rather than work somebody decided against.
        var open = Tasks()
            .Split('\n')
            .Where(line => line.StartsWith("- [ ]", StringComparison.Ordinal))
            .ToList();

        Assert.All(
            open,
            line => Assert.True(
                line.Contains("Open", StringComparison.OrdinalIgnoreCase)
                || line.Contains("needs the owner", StringComparison.OrdinalIgnoreCase),
                $"an open task says nothing about why: {line.Trim()}"));
    }

    [Fact]
    public void The_one_task_left_open_is_the_one_that_needs_a_person()
    {
        // Everything a machine can check has been checked; what remains is a walk on somebody's
        // live deployment. If that count ever grows, the reason each was left has to grow with it.
        var open = Tasks()
            .Split('\n')
            .Count(line => line.StartsWith("- [ ]", StringComparison.Ordinal));

        Assert.Equal(1, open);
        Assert.Contains("T081", Tasks(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_says_what_006_reversed_and_what_settled_it()
    {
        // The most valuable thing in a feature's record is the argument, not the inventory. 006
        // reversed a decision 005 wrote down as settled; a record that listed the outcome without
        // the reasoning would leave the next reader to rediscover why the old reasoning was wrong.
        var tasks = Flowed(Tasks());

        Assert.Contains("T039", tasks, StringComparison.Ordinal);
        Assert.Contains("stop rule", tasks, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_diagnostic_still_in_the_code_is_named_in_the_record()
    {
        // The layout threshold is 200 because the owner asked for it and has not yet looked. That
        // is a temporary value in shipped code, which is exactly the kind of thing that becomes
        // permanent by nobody remembering it was temporary.
        var tasks = Flowed(Tasks());
        var shell = Read("src", "Parley.Top", "Shell.cs");

        if (shell.Contains("internal const int Threshold = 200;", StringComparison.Ordinal))
        {
            Assert.Contains("threshold stays at 200", tasks, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("diagnostic", Flowed(shell), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_parity_the_record_claims_names_both_platforms()
    {
        // A parity table that named one platform would be a claim about the machine it was run on.
        var tasks = Tasks();

        Assert.Contains("Windows", tasks, StringComparison.Ordinal);
        Assert.Contains("Linux", tasks, StringComparison.Ordinal);
    }

    [Fact]
    public void The_traceability_table_covers_every_requirement_the_spec_numbers()
    {
        // Added during the first gate, and the thing that would rot first: a requirement added
        // later with no row. Checked against the spec rather than against itself.
        var spec = Read("specs", "006-three-view-operator", "spec.md");
        var tasks = Tasks();

        var numbers = Regex.Matches(spec, @"FR-0\d\d", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(numbers);

        var missing = numbers.Where(number => !tasks.Contains(number, StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0, "no traceability row for: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_check_is_reading_the_record()
    {
        Assert.True(Tasks().Length > 10_000);
        Assert.Contains("- [X]", Tasks(), StringComparison.Ordinal);
    }
}
