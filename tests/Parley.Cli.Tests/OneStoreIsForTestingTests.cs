using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That nothing the tools print tells a person to deploy with the one-store options, which are for testing only.
/// </summary>
/// <remarks>
/// <para>
/// A machine is deployed as projects: `parley project add` names each one's store and endpoint, and the logon entry
/// serves them all. `serve --db`, `autostart install --db` and <c>PARLEY_DB</c> serve one store outside the manifest,
/// so a test rig need name no project, as the README, the help and the skill say.
/// </para>
/// <para>
/// <b>The two places a person is most likely to read it</b> are the refusal `parley serve` gives with nothing
/// named, and the next steps `parley enroll apply` prints. Neither may offer only the one-store route. Read off the source, because the sentence is what matters and the paths that print it need a
/// broker and a set of repositories to reach.
/// </para>
/// </remarks>
public sealed class OneStoreIsForTestingTests
{
    public static TheoryData<string> Printing => ["src/Parley.Setup/EnrollCommand.cs", "src/Parley.Cli/ServeCommand.cs", "src/Parley.Cli/AutostartCommand.cs"];

    /// <summary>Every source that prints a command for an operator to type next.</summary>
    /// <remarks>
    /// Wider than <see cref="Printing"/> because the defect this catches was not about one store.
    /// <c>src/Parley.WebUi</c> is deliberately absent: <c>parley-web-ui --listen</c> is that tool's
    /// own option and has nothing to do with where a project's broker answers.
    /// </remarks>
    public static TheoryData<string> Offering =>
    [
        "src/Parley.Setup/EnrollCommand.cs",
        "src/Parley.Setup/ProjectCommand.cs",
        "src/Parley.Cli/ServeCommand.cs",
        "src/Parley.Cli/AutostartCommand.cs",
        "src/Parley.Cli/Program.cs",
        "src/Parley.Terminal/Naming.cs",
    ];

    /// <summary>The removed spellings, as a person would be told to type them.</summary>
    /// <remarks>
    /// <para>
    /// Exact phrases rather than bare flag names, because two of these flags still exist on other
    /// commands: <c>--node-config</c> names this machine's catalogue for <c>serve</c>,
    /// <c>autostart</c> and <c>up</c>, and <c>--listen</c> is <c>parley-web-ui</c>'s own. Banning
    /// the words would fail on every legitimate use and be suppressed within a week.
    /// </para>
    /// <para>
    /// <c>project add &lt;name&gt; --</c> has no exception: the name is the whole of the command, so
    /// any flag offered after it is one that no longer exists.
    /// </para>
    /// </remarks>
    private static readonly string[] Removed =
    [
        "project add <name> --",
        "project set <name> --node-config",
        "autostart install --db",
        "serve --db",
        "with no --db",
    ];

    [Theory]
    [MemberData(nameof(Offering))]
    public void No_source_offers_a_command_whose_flags_were_removed(string source)
    {
        // **The class of defect, rather than one case.** `--db`, `--listen` and the per-project
        // `--node-config` are not in the parser, and must not stand in the text the tools print, or
        // three separate places would tell an operator to run a command that would be refused:
        // `project`'s own usage, `enroll apply`'s next steps, and the autostart advice repeated in
        // both the CLI and the terminal.
        //
        // Removing a flag is a sweep over prose, and prose is what nothing else here checks.
        //
        // **Comments are excluded, and the first run of this check is why.** It failed on two files
        // whose comments quote the removed spelling in order to record that it was removed — which is
        // the documentation style this repository asks for, and the only place a reader learns why a
        // flag they remember is gone. A guard that forbade explaining a removal would be answered by
        // deleting the explanation, so it would make the codebase worse while reading as stricter.
        // What is checked is what the tool would *print*.
        var text = string.Join(
            '\n',
            RepositoryFile.Read(source)
                .Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();

                    return !trimmed.StartsWith("//", StringComparison.Ordinal)
                        && !trimmed.StartsWith("*", StringComparison.Ordinal);
                }));

        foreach (var removed in Removed)
        {
            Assert.DoesNotContain(removed, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Printing))]
    public void No_printed_step_deploys_with_one_store(string source)
    {
        var text = RepositoryFile.Read(source);

        Assert.DoesNotContain("parley serve --db <path to the store>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("autostart install --db <path to the store>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolment_names_the_project_and_the_entry_that_serves_it()
    {
        var text = RepositoryFile.Read("src/Parley.Setup/EnrollCommand.cs");

        // **The step is asserted, not the flags.** What the step promises is that a person with no
        // project is told how to name one and how it comes to be served. Naming a project is the
        // terminal's, so a step naming any `project` command at all would send somebody to one the
        // tool refuses. The same defect as the one above, which is why the check is on the step
        // rather than on any command's arguments.
        Assert.Contains("       parley\\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("parley project add", text, StringComparison.Ordinal);
        Assert.Contains("parley autostart install", text, StringComparison.Ordinal);
    }
}
