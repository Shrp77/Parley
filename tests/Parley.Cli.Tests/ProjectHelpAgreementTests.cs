using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Every project subcommand the code dispatches is described in all three places a person reads.
/// </summary>
/// <remarks>
/// Found by lens 3: <c>project set</c> was added with its usage line and its help entry and
/// without a word in the README, which is the document the plugin and the install script send
/// people to. Read from the dispatch rather than listed here, so a subcommand added tomorrow is
/// checked tomorrow.
/// </remarks>
public sealed partial class ProjectHelpAgreementTests
{
    [Fact]
    public void Every_dispatched_project_subcommand_is_in_the_help_the_usage_and_the_readme()
    {
        var command = RepositoryFile.Read("src/Parley.Setup/ProjectCommand.cs");
        var help = RepositoryFile.Read("src/Parley.Cli/Program.cs");
        var readme = RepositoryFile.Read("README.md");

        var dispatched = Dispatch().Matches(command)
            .Select(m => m.Groups["verb"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Non-vacuity: the pattern found the switch, and the switch has what it has today.
        Assert.Equal(["add", "list", "remove", "set"], dispatched);

        foreach (var verb in dispatched)
        {
            var spelled = $"project {verb}";

            Assert.Contains(spelled, help, StringComparison.Ordinal);
            Assert.Contains(spelled, command, StringComparison.Ordinal);
            Assert.Contains(spelled, readme, StringComparison.Ordinal);
        }

        // The one flag that deletes is named wherever remove is.
        Assert.Contains("--destroy", help, StringComparison.Ordinal);
        Assert.Contains("--destroy", command, StringComparison.Ordinal);
        Assert.Contains("--destroy", readme, StringComparison.Ordinal);
    }

    /// <summary>A list-pattern arm of the dispatch: <c>["list"]</c> or <c>["add", var name]</c>.</summary>
    [GeneratedRegex("""\[\s*"(?<verb>[a-z]+)"(?:\s*,\s*var\s+\w+)?\s*\]\s*=>""")]
    private static partial Regex Dispatch();
}
