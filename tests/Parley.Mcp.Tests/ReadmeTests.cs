using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the README stands on its own and matches what the software actually reads.
/// </summary>
/// <remarks>
/// <para>
/// The README is read on GitHub by somebody who has not opened SPEC.md and is not going to. That
/// makes it the one document whose correctness is entirely about what a stranger can act on, and
/// two failures of that kind had already reached it: a launch configuration missing two of the
/// variables its server reads, and prose that explained itself by citing specification sections
/// the reader cannot follow.
/// </para>
/// <para>
/// The same discipline as <c>SpecifiedOperationTests</c>: documentation that tells someone what
/// to type is documentation the code can be measured against. Everything here is deliberately
/// weak about phrasing — pinning wording would fail the build every time somebody improved a
/// sentence — and strict about the two things a reader cannot recover on their own.
/// </para>
/// </remarks>
public sealed class ReadmeTests
{
    /// <summary>The README, normalised and located from the test assembly.</summary>
    /// <returns>Its text.</returns>
    private static string Readme() => RepositoryFile.Read("README.md");

    /// <summary>Every variable name the server or the operator's tool reads.</summary>
    /// <returns>The names, from the constants that define them.</returns>
    private static IReadOnlyList<string> Variables() => DeploymentVariables.All();

    [Fact]
    public void Every_variable_a_server_reads_appears_in_the_readme()
    {
        var readme = Readme();

        var undocumented = Variables()
            .Where(v => !readme.Contains(v, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(undocumented);
    }

    [Fact]
    public void The_launch_configuration_example_is_the_whole_set()
    {
        // Mentioned somewhere is not the same as shown where a deployment is configured. The
        // retention pair was documented — under the operator's command, which is exactly the
        // place that made it look like somebody else's problem, while in fact the servers are
        // what apply the policy and they do it by deleting message text.
        var example = Regex.Match(Readme(), @"""mcpServers"".*?\n```", RegexOptions.Singleline);

        Assert.True(example.Success, "the README no longer shows a launch configuration");

        var missing = Variables()
            .Where(v => !example.Value.Contains(v, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_readme_explains_itself_without_citing_the_specification()
    {
        // A section sign in the README is a sentence that has outsourced its meaning. It reads
        // as a footnote to a document the reader does not have open, and there is no link to
        // follow — SPEC.md is thousands of lines and GitHub will not scroll them to §9.4.
        //
        // The rule is that the README says the thing rather than citing where the thing is
        // written. Linking to SPEC.md as a whole is fine and encouraged; nineteen references to
        // numbered sections of it were not.
        var offenders = Readme()
            .Split('\n')
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(l => l.Line.Contains('§', StringComparison.Ordinal))
            .Select(l => $"line {l.Number}: {l.Line.Trim()}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_readme_says_how_an_agent_comes_to_know_about_parley()
    {
        // The step that makes it work, and the one the README omitted entirely: binding the
        // tools tells an agent how to ask, not that there is anyone to ask. Without standing
        // instructions naming the peer, an agent answers from its own repository and guesses at
        // the rest — which is the failure Parley exists to prevent, reached by installing it
        // correctly and stopping one step early.
        //
        // Checked by the vocabulary a reader needs to find, not by phrasing: where the
        // instructions go for the two conventions in common use, and the fact that they are what
        // makes an agent reach for the tools at all.
        var readme = Readme();

        foreach (var needed in new[] { "CLAUDE.md", "AGENTS.md", "parley_ask", "parley_inbox" })
        {
            Assert.True(
                readme.Contains(needed, StringComparison.Ordinal),
                $"the README no longer mentions {needed}, which a reader needs to get two agents talking");
        }
    }
}
