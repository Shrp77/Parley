using System.Globalization;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// The clarify cap default, as the documents state it and as the code applies it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the number is expected to move. It is 200 as a measurement rather than as
/// a considered limit — SPEC.md §17 question 4 says to lower it once <c>clarifications()</c>
/// reports where depth actually settles — and whoever does that will change one constant and a
/// test, then ship documents still telling operators the default is 200. The rot is silent,
/// because prose has nothing checking it.
/// </para>
/// <para>
/// It lives here rather than in <c>Parley.Spec.Tests</c> deliberately. That project references
/// neither Core nor Store, so that a check on the document cannot drift into asserting what this
/// build happens to do — a boundary worth keeping, and this assertion is exactly the kind that
/// would breach it. Here the reference is ordinary.
/// </para>
/// <para>
/// The assertion is a substring rather than a parse. What is being defended is that a reader of
/// either document is told the number the broker will apply; recovering the value from prose is
/// not worth the fragility, and a phrase that no longer appears at all fails just as loudly.
/// </para>
/// </remarks>
public sealed class DocumentedDefaultTests
{
    private static string Cap =>
        ExchangePolicy.Default.ClarifyDepthCap.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void The_specification_states_the_default_the_domain_applies()
    {
        Assert.Contains(
            $"Clarify depth is capped (default {Cap})",
            RepositoryFile.Read("SPEC.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_worst_case_in_the_specification_is_computed_from_that_default()
    {
        // §5.3 multiplies the cap by the message cap to say what one exchange can place in a
        // peer's context. Lowering the cap without redoing that arithmetic leaves the section
        // that exists to gather this consequence in one place stating the wrong figure — which
        // is worse than not stating it, because it reads as having been checked.
        var worstCase = (ExchangePolicy.Default.ClarifyDepthCap + 1) * MessageLimits.MaxPayloadCharacters;

        Assert.Contains(
            $"`({Cap} + 1) × {MessageLimits.MaxPayloadCharacters:N0}`",
            RepositoryFile.Read("SPEC.md"),
            StringComparison.Ordinal);

        Assert.Contains(
            $"about {worstCase:N0} characters",
            RepositoryFile.Read("SPEC.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_offers_the_default_as_its_example()
    {
        Assert.Contains(
            $"\"PARLEY_CLARIFY_CAP\": \"{Cap}\"",
            RepositoryFile.Read("README.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_writes_the_default_it_documents()
    {
        // Two claims in one file: the value written into a fresh configuration, and the sentence
        // telling the user what an absent variable would have done. Both are the same number and
        // both are prose, so both rot the same way.
        var setup = RepositoryFile.Read("plugins/parley/commands/setup.md");

        Assert.Contains($"\"PARLEY_CLARIFY_CAP\": \"{Cap}\"", setup, StringComparison.Ordinal);
        Assert.Contains($"{Cap} is also what the server uses", setup, StringComparison.Ordinal);
    }
}
