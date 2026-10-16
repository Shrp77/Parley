using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// The clarify cap default, as the documents state it and as the code applies it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the number can move: there is no cap by default, on the reasoning of SPEC.md
/// principle 2.8 — nothing cuts a line of work off before its participants are finished. Whoever changes the default changes a constant and a test,
/// and would otherwise ship documents still telling operators the old number. Prose has nothing
/// checking it but this.
/// </para>
/// <para>
/// It lives here rather than in <c>Parley.Spec.Tests</c> deliberately. That project references
/// neither Core nor Store, so a check on the document cannot drift into asserting what this build
/// happens to do. Here the reference is ordinary.
/// </para>
/// <para>
/// The assertions are substrings rather than a parse. What is defended is that a reader of either
/// document is told what the broker will do; recovering that from prose is not worth the fragility,
/// and a phrase that no longer appears at all fails just as loudly.
/// </para>
/// </remarks>
public sealed class DocumentedDefaultTests
{
    [Fact]
    public void The_specification_says_clarifications_are_uncapped_and_the_domain_agrees()
    {
        Assert.False(ExchangePolicy.Default.BoundsClarifications);

        Assert.Contains(
            "Depth is not capped unless a deployment caps it",
            RepositoryFile.Read("SPEC.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_specification_says_what_an_uncapped_exchange_can_carry()
    {
        // With no clarification cap, the message cap bounds a message and nothing bounds an
        // exchange. Rule 5 says so rather than leaving a reader to work it out, and it is the
        // sentence a later decision to set a cap would have to argue with.
        Assert.Contains(
            "It bounds one message, not an exchange",
            RepositoryFile.Read("SPEC.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_states_the_default_the_domain_applies()
    {
        var readme = RepositoryFile.Documentation();

        Assert.False(ExchangePolicy.Default.BoundsClarifications);
        Assert.Contains("Clarifications are not capped", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("admits 200", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_does_not_offer_a_cap_it_cannot_set()
    {
        // This asserted that the setup command wrote the cap into every launch configuration,
        // and that the sentence beside it named the same number. Both were right while each
        // repository set its own.
        //
        // A launch configuration cannot set it now, so writing it would be worse than omitting
        // it: the user would see a value in the file they were told to create, change it, and
        // find that nothing happened. The check is therefore that it is absent, which is a real
        // property and not the absence of a test.
        var setup = RepositoryFile.Read("plugins/parley/commands/setup.md");

        var configuration = System.Text.RegularExpressions.Regex.Match(
            setup, @"""mcpServers"".*?\n```",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.True(configuration.Success, "the setup command no longer shows a configuration");

        Assert.DoesNotContain(
            "PARLEY_CLARIFY_CAP", configuration.Value, StringComparison.Ordinal);
    }
}
