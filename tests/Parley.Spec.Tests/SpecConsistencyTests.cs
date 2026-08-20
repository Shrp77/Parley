using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// Checks SPEC.md against itself: references that resolve, tables that are not ragged, names
/// that do not collide.
/// </summary>
public sealed class SpecConsistencyTests
{
    [Fact]
    public void Every_section_reference_resolves()
    {
        // Sections have been renumbered and split repeatedly, and a reference to a heading
        // that no longer exists reads as authoritative while pointing at nothing.
        var dangling = SpecDocument.SectionReferences
            .Where(r => !SpecDocument.Headings.Contains(r))
            .ToList();

        Assert.True(dangling.Count == 0,
            $"SPEC.md references sections that do not exist: {string.Join(", ", dangling.Select(d => "§" + d))}");
    }

    [Fact]
    public void No_table_has_a_ragged_row()
    {
        // A row with the wrong number of cells renders as a silently shifted table: every
        // value after the missing pipe describes the wrong column.
        var ragged = SpecDocument.Tables
            .Where(rows => rows.Select(r => r.Split('|').Length).Distinct().Count() > 1)
            .Select(rows => rows[0])
            .ToList();

        Assert.True(ragged.Count == 0,
            $"SPEC.md has tables with inconsistent column counts, starting: {string.Join(" / ", ragged)}");
    }

    [Fact]
    public void Agent_and_operator_operations_do_not_share_a_name()
    {
        // §10.2 opens by warning that an adapter surfacing an operator operation into an
        // agent's tool set would breach §10.1. Two operations sharing a name across exactly
        // that boundary is how that mistake gets made: the wrong one is bound, the signatures
        // nearly match, and an agent quietly acquires an operator's authority. This caught a
        // real collision, where `conclude` existed on both sides.
        var agent = OperationNames("10.1");
        var operators = OperationNames("10.2");

        var shared = agent.Intersect(operators, StringComparer.Ordinal).ToList();

        Assert.True(shared.Count == 0,
            $"operations appear in both §10.1 and §10.2: {string.Join(", ", shared)}");
    }

    [Fact]
    public void Rules_of_engagement_are_numbered_contiguously()
    {
        var numbers = Regex.Matches(SpecDocument.Section("8"), @"^(\d+)\. \*\*", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(numbers);
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    [Fact]
    public void The_rule_enforcement_table_covers_every_rule()
    {
        // §8 carries a table saying what holds each rule, which is only honest while it lists
        // all of them. A rule added to the list and not the table reads as absent from the
        // audit rather than as unaudited.
        var section = SpecDocument.Section("8");

        var rules = Regex.Matches(section, @"^(\d+)\. \*\*", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var tabulated = Regex.Matches(section, @"^\| (\d+(?:, \d+)*) \| ", RegexOptions.Multiline)
            .SelectMany(m => m.Groups[1].Value.Split(", "))
            .ToHashSet(StringComparer.Ordinal);

        var missing = rules.Except(tabulated, StringComparer.Ordinal).OrderBy(r => r).ToList();

        Assert.True(missing.Count == 0,
            $"§8 lists rules absent from its enforcement table: {string.Join(", ", missing)}");
    }

    private static IReadOnlyList<string> OperationNames(string section) =>
        Regex.Matches(SpecDocument.Section(section), @"^\| `([a-z_]+)\(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
}
