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
        // place that made it look like somebody else's problem, while in fact something was
        // applying the policy by deleting message text.
        //
        // The set this covers narrowed when the broker became its own process, and the check
        // narrowed with it rather than being relaxed: what a launch configuration must show is
        // every variable an *adapter* reads. The deployment-level ones moved to `parley serve`,
        // and the test below is what keeps them from becoming undocumented on the way.
        var example = Regex.Match(Readme(), @"""mcpServers"".*?\n```", RegexOptions.Singleline);

        Assert.True(example.Success, "the README no longer shows a launch configuration");

        var missing = DeploymentVariables.Adapter()
            .Where(v => !example.Value.Contains(v, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_variable_the_broker_reads_is_shown_where_the_broker_is_started()
    {
        // The other half. These are the variables that decide what is deleted and when
        // deadlines are enforced, and they are read by one process — so the place they have to
        // appear is the section that says how to start it. Documented under the launch
        // configuration instead, they would read as something each repository sets, which is
        // exactly the arrangement that was removed.
        // Found by what the section contains rather than by what it is called. This used to
        // match the heading "Start the broker" literally, and it failed the day that heading was
        // renamed -- against a README where every variable was still correctly documented, in
        // the same section, under a better title. A check that fires on retitling teaches people
        // to change the check.
        var section = Regex.Matches(Readme(), @"### \d\..*?(?=\n### |\n## )", RegexOptions.Singleline)
            .FirstOrDefault(m => m.Value.Contains("parley serve", StringComparison.Ordinal));

        Assert.True(
            section is not null,
            "the README no longer has a section describing `parley serve`, which is where the "
            + "variables that configure the whole deployment have to be documented.");

        var missing = DeploymentVariables.Broker()
            .Where(v => !section!.Value.Contains(v, StringComparison.Ordinal))
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

        foreach (var needed in new[] { "CLAUDE.md", "AGENTS.md", "parley_request", "parley_inbox" })
        {
            Assert.True(
                readme.Contains(needed, StringComparison.Ordinal),
                $"the README no longer mentions {needed}, which a reader needs to get two agents talking");
        }
    }

    [Fact]
    public void The_readme_does_not_describe_the_architecture_that_was_replaced()
    {
        // These sentences were true of the deployment where every adapter opened the store
        // itself, and they survived the change to a broker process — in the same file that,
        // seventy lines earlier, tells the reader to install the broker under a supervisor. A
        // README that contradicts itself is worse than one that is merely out of date, because
        // both halves read as current and the reader believes whichever they meet first.
        //
        // This one is not academic. A reader who believes there is nothing to supervise skips
        // `autostart install`, and every agent then fails to attach with "no broker is
        // listening" — which is exactly the failure the install script was rewritten to prevent,
        // taught by the document that is supposed to prevent it.
        //
        // Phrases rather than a general check, because the claim is specific and a vaguer test
        // would either pass over the next version of this mistake or fail over a sentence that
        // is fine. Each entry is a statement the broker made false.
        var readme = Readme();

        string[] replaced =
        [
            "no daemon",
            "no service to supervise",
            "Nothing survives a reboot",
            "zero Parley processes",
            "the state is in the file"
        ];

        var offenders = replaced
            .Where(claim => readme.Contains(claim, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "the README makes claims the broker process replaced: " + string.Join("; ", offenders));
    }
}
