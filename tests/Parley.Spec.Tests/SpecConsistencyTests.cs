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
    public void An_instruction_is_bounded_by_the_same_number_as_a_message()
    {
        // Two caps on text that lands in the same place, and the whole reason for reusing the
        // number rather than choosing a second one. SQLite cannot express "the same as that
        // one", so nothing but this stops the pair drifting -- and they would drift at the worst
        // moment, when evidence moves the message cap and the instruction cap is left behind at
        // a number nobody argued for.
        var caps = Regex.Matches(
                SpecDocument.Text,
                @"CHECK \(length\((?<column>payload_json|text)\) <= (?<cap>\d+)\)")
            .Select(m => (Column: m.Groups["column"].Value, Cap: m.Groups["cap"].Value))
            .ToList();

        Assert.Contains(caps, c => c.Column == "payload_json");
        Assert.Contains(caps, c => c.Column == "text");

        Assert.True(
            caps.Select(c => c.Cap).Distinct().Count() == 1,
            "a message and an instruction are capped at different lengths: "
            + string.Join(", ", caps.Select(c => $"{c.Column} <= {c.Cap}")));
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

    [Fact]
    public void The_wire_contract_has_no_notion_of_a_project()
    {
        // SPEC.md §4.1: "The protocol has no concept of a project, and must not acquire one."
        // Membership is which endpoint an agent connected to, and that is load-bearing rather than
        // stylistic — it is why there is no group column, no scoped query, and no WHERE clause
        // anyone can forget, and why agent names may collide across projects at all.
        //
        // Nothing enforced it. A machine-level manifest of named projects now exists, every
        // command takes --project, and an adapter can be launched with PARLEY_PROJECT — so the
        // obvious next step for somebody who has not read §4.1 is a project field on a request, at
        // which point isolation stops being a property of the file and starts being a property of
        // code remembering to filter. That is the trade §4.1 exists to refuse.
        //
        // Checked against the .proto because that is the contract another implementation reads.
        //
        // Comments are stripped first rather than excluded by a lookahead. The lookahead version of
        // this — `^\s*(?!//).*` — does not work: the engine backtracks `\s*` by one space, the
        // lookahead then sees " /" instead of "//", and the line matches after all. So a comment
        // saying why there is deliberately no project field would have failed this check, which is
        // how a guard earns a reputation for crying wolf and gets deleted.
        var code = Regex.Replace(SpecDocument.Proto, @"//.*$", string.Empty, RegexOptions.Multiline);

        var offending = Regex.Matches(code, @"^.*\bproject\b.*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(m => m.Value.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        Assert.True(
            offending.Count == 0,
            "The wire contract mentions a project outside a comment, which SPEC.md §4.1 forbids:" +
            Environment.NewLine + string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void The_documented_schema_has_no_notion_of_a_project_either()
    {
        // The other half, and the one §9.3 cares about: invariants belong in storage because
        // application code can route around them. A project column would be a scope the code has
        // to remember to apply on every query, which is precisely what separate database files
        // make impossible.
        var schema = string.Join("\n", SpecDocument.SchemaFragments);

        Assert.DoesNotContain("project", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("unarchive")]
    [InlineData("destroy")]
    [InlineData("start")]
    public void The_operations_006_added_are_operator_operations_and_stay_there(string operation)
    {
        // Named individually rather than checked as a set, because the failure this guards is one
        // operation moving, and a set assertion would report only that the sets differ.
        //
        // The generic check above already fails when a name appears on both sides. This fails when
        // a name appears on the *wrong* side alone -- an operation quietly relocated to §10.1,
        // which the shared-name check cannot see because after such a move nothing is shared.
        Assert.Contains(operation, OperationNames("10.2"));
        Assert.DoesNotContain(operation, OperationNames("10.1"));
    }

    [Fact]
    public void The_boundary_check_is_reading_operations_at_all()
    {
        // Both checks above pass vacuously against a section this regex fails to match, which is
        // the trap the schema extraction already had to be guarded against. Ten is well under the
        // real count and well over zero.
        Assert.True(OperationNames("10.1").Count >= 5, "no agent operations were extracted");
        Assert.True(OperationNames("10.2").Count >= 10, "no operator operations were extracted");
    }

    private static IReadOnlyList<string> OperationNames(string section) =>
        Regex.Matches(SpecDocument.Section(section), @"^\| `([a-z_]+)\(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
}
