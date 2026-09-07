using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// That 006's requirements are each answerable, and that its documents still describe what was
/// built.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.3. Runs 3.1 and 3.2 looked at the code — what the shared
/// library grew and whether either surface went round it. This looks at the documents, which age
/// differently: they are written before the work and are the last thing anybody rereads.
/// </para>
/// <para>
/// <b>Here rather than in a surface's tests, because this project references neither Core nor
/// Store.</b> That is deliberate and it is what stops a check about a document drifting into a
/// check about the code — the same reason `Parley.Spec.Tests` exists at all.
/// </para>
/// </remarks>
public sealed class FeatureTraceTests
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

    private static string Feature(params string[] under) =>
        File.ReadAllText(Path.Combine([Root(), "specs", "006-three-view-operator", .. under]));

    private static string Everything()
    {
        var at = Path.Combine(Root(), "specs", "006-three-view-operator");

        return string.Join(
            "\n",
            Directory.EnumerateFiles(at, "*.md", SearchOption.AllDirectories).Select(File.ReadAllText));
    }

    [Fact]
    public void Every_requirement_the_spec_numbers_is_referred_to_somewhere_else()
    {
        // A number nothing else in the feature mentions is a requirement that was written and then
        // never assigned to anybody -- which is how one gets built by accident or not at all.
        var spec = Feature("spec.md");
        var rest = Everything().Replace(spec, string.Empty, StringComparison.Ordinal);

        var numbers = Regex.Matches(spec, @"FR-0\d\d", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(numbers);

        var orphaned = numbers
            .Where(number => !rest.Contains(number, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            "no plan, contract, task or research entry mentions: " + string.Join(", ", orphaned));
    }

    [Fact]
    public void The_plan_settles_every_question_the_spec_left_open()
    {
        // The spec's own checklist named three. A plan that answered two would leave the third to
        // be decided by whoever happened to write the code, which is the decision-by-default this
        // whole process exists to avoid.
        var plan = Feature("plan.md");

        foreach (var settled in new[] { "Q1 settled", "Q2 settled", "Q3 settled" })
        {
            Assert.Contains(settled, plan, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_contract_no_longer_holds_anything_back_from_a_surface()
    {
        // The pending table was the mechanism for landing the protocol before the views. It has to
        // empty out, and a feature that shipped with rows still in it would be one where DriftTests
        // is exempting something nobody remembered.
        var contract = Feature("contracts", "operations.md");
        var pending = contract[contract.IndexOf(
            "## Not yet bound by either surface", StringComparison.Ordinal)..];

        Assert.DoesNotContain("| `Archive", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("| `Unarchive", pending, StringComparison.Ordinal);
        Assert.Contains("Empty", pending, StringComparison.Ordinal);
    }

    [Fact]
    public void The_four_operations_are_in_the_specification_and_in_the_wire_contract()
    {
        // The document is the product and the proto is what another implementation reads. An
        // operation in one and not the other is a contract that cannot be built against.
        var spec = File.ReadAllText(Path.Combine(Root(), "SPEC.md"));
        var proto = File.ReadAllText(Path.Combine(Root(), "proto", "parley.proto"));

        foreach (var (named, rpc) in new[]
                 {
                     ("`archive(conversation)`", "rpc Archive"),
                     ("`unarchive(conversation)`", "rpc Unarchive"),
                     ("`destroy(conversation)`", "rpc Destroy"),
                     ("`start(agent)`", "rpc Start"),
                 })
        {
            Assert.Contains(named, spec, StringComparison.Ordinal);
            Assert.Contains(rpc, proto, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_quickstart_walks_the_things_the_feature_actually_added()
    {
        // The document somebody follows to decide whether it works. A quickstart that still
        // described the previous shape would be the feature's own acceptance criteria pointing at
        // what it replaced.
        var quickstart = Feature("quickstart.md");

        foreach (var step in new[] { "Archive", "Destroy", "F8", "startable" })
        {
            Assert.Contains(step, quickstart, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("four regions", quickstart, StringComparison.OrdinalIgnoreCase);
    }
}
