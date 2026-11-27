using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the command line still offers for projects, and what it no longer does.
/// </summary>
/// <remarks>
/// <para>
/// A subcommand such as <c>project set</c> with a usage line and a help entry and
/// without a word in the README would leave out the document the plugin and the install script send
/// people to. Read from the dispatch rather than listed here, so a subcommand added tomorrow is
/// checked tomorrow.
/// </para>
/// <para>
/// <b>Its premise changed on 2026-09-25 and the test changed with it.</b> It asserted that every
/// <i>dispatched</i> subcommand was offered in the help — which was the same set as what a person
/// could type, until <c>add</c>, <c>set</c> and <c>remove</c> became the terminal's. They are
/// still dispatched, because the terminal calls this same command in process; they are simply no
/// longer reachable from a shell. So the two halves are now asked separately, and the second half
/// is the stronger one: a moved subcommand creeping back into <c>parley --help</c> fails here.
/// </para>
/// </remarks>
[Integration]
public sealed partial class ProjectHelpAgreementTests
{
    /// <summary>The subcommands the command line refuses and the terminal performs.</summary>
    private static readonly string[] Moved = ["add", "set", "remove"];

    [Fact]
    public void Every_dispatched_project_subcommand_is_still_described_in_the_readme()
    {
        var command = RepositoryFile.Read("src/Parley.Setup/ProjectCommand.cs");
        var readme = RepositoryFile.Documentation();

        var dispatched = Dispatch().Matches(command)
            .Select(m => m.Groups["verb"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Non-vacuity: the pattern found the switch, and the switch still has all four. The arms
        // did not go anywhere -- the terminal reaches every one of them.
        Assert.Equal(["add", "list", "remove", "set"], dispatched);

        // The README describes what a person can do, not which arm runs it, so a moved subcommand
        // is described by name rather than as a command. `destroy` is the exception worth pinning:
        // it deletes a store, and a document that stopped mentioning that is a document somebody
        // reads before deciding it is safe.
        Assert.Contains("Destroying one", readme, StringComparison.Ordinal);
        Assert.Contains("project list", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_offers_only_what_the_command_line_will_still_do()
    {
        // **The half that pins the removal.** `parley --help` is where somebody looks for what they
        // can type, so a line offering a subcommand the tool now refuses is worse than no line at
        // all: they type it, are refused, and the tool contradicted its own manual.
        var help = RepositoryFile.Read("src/Parley.Cli/Program.cs");
        var manual = Manual(help);

        Assert.Contains("parley project list", manual, StringComparison.Ordinal);

        foreach (var verb in Moved)
        {
            Assert.DoesNotContain(
                $"parley project {verb}",
                manual,
                StringComparison.Ordinal);
        }
    }

    /// <summary>The help text itself, without the code and comments around it.</summary>
    /// <param name="source">Program.cs.</param>
    /// <returns>What `parley --help` prints.</returns>
    /// <remarks>
    /// Read out of the raw string the help lives in rather than from the whole file, because the
    /// refusals below it necessarily name what they refuse — and a check over the whole source
    /// would be answered by the very sentence that tells somebody where the command went.
    /// </remarks>
    private static string Manual(string source)
    {
        const string Opens = "const string Help = \"\"\"";

        var from = source.IndexOf(Opens, StringComparison.Ordinal);

        Assert.True(from >= 0, "the help text is no longer a raw string literal called Help");

        var to = source.IndexOf("\"\"\";", from, StringComparison.Ordinal);

        Assert.True(to > from, "the help text's raw string literal does not end");

        return source[from..to];
    }

    /// <summary>A list-pattern arm of the dispatch: <c>["list"]</c> or <c>["add", var name]</c>.</summary>
    [GeneratedRegex("""\[\s*"(?<verb>[a-z]+)"(?:\s*,\s*var\s+\w+)?\s*\]\s*=>""")]
    private static partial Regex Dispatch();
}
