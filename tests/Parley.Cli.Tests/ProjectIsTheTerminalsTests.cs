using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Creating, changing and removing a project left the command line; listing them did not.
/// </summary>
/// <remarks>
/// <para>
/// The owner's call on 2026-09-25, in their own words: <i>"if we're doing everything in the
/// terminal app, then we DO everything in the terminal app."</i> It follows the sixteen operator
/// operations that moved the day before, and finishes the job those left half done — a person was
/// still expected to open a shell to bring a deployment into existence.
/// </para>
/// <para>
/// <b>Refused at the entry point rather than deleted from <c>ProjectCommand</c>, and the
/// difference is load-bearing.</b> The terminal does not reimplement naming a project: <c>Naming</c>
/// and <c>Removing</c> call that same command in process and print what it said, which is what
/// makes a machine configured from a screen configured exactly as one configured from a shell.
/// Deleting the arms would have taken the terminal's own creation and removal with them. So every
/// arm survives and what went is the dispatch in <c>parley</c>.
/// </para>
/// <para>
/// <b>Driven through the real binary</b>, because the refusal is in the entry point and an
/// in-process call never reaches it — the same reason <c>MovedToTheTerminalTests</c> is driven, and
/// the same trap that hid half of <c>parley files</c> being unreachable from a shell while every
/// in-process test passed.
/// </para>
/// </remarks>
public sealed class ProjectIsTheTerminalsTests
{
    /// <summary>The subcommands a shell no longer reaches.</summary>
    /// <remarks>
    /// Spelled out rather than read back out of the source, for the reason the sixteen verbs are:
    /// a list derived from the file it checks passes against a build that quietly dropped one.
    /// </remarks>
    public static TheoryData<string> Moved => ["add", "set", "remove"];

    [Theory]
    [MemberData(nameof(Moved))]
    public void It_exits_sixty_four_and_names_the_terminal(string action)
    {
        var (code, error) = ToolProcess.Run([], "project", action, "anything");

        // EX_USAGE, so a script detects it rather than reading an empty success (FR-051).
        Assert.Equal(64, code);
        Assert.Contains("parley terminal", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Moved))]
    public void It_says_so_before_anything_is_written(string action)
    {
        // **The refusal comes before the command runs, which is what makes it safe to test with a
        // real name.** If the arm were reached and refused inside itself, `remove` would have
        // already taken a lock and `add` would have already claimed a port -- and this test would
        // be creating projects on the machine running the suite.
        var (_, error) = ToolProcess.Run([], "project", action, "orders");

        Assert.Contains("is not a command here any more", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_still_works_because_both_installers_call_it()
    {
        // **FR-050, as a guard rather than a promise.** `install.ps1` and `install.sh` both read
        // `project list` -- for the manifest path, and to check a project's broker actually answers
        // rather than that a task was registered. It also reads nothing else and writes nothing, so
        // it is not one of the acts this move is about.
        var (code, _, error) = ToolProcess.Reading("project", "list");

        Assert.NotEqual(64, code);
        Assert.DoesNotContain("is not a command here any more", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_usage_it_prints_offers_only_listing()
    {
        // `parley project` with nothing after it falls through to the usage, which is the other
        // place somebody reads what they can type. It named all four until this change.
        var (_, error) = ToolProcess.Run([], "project");

        Assert.Contains("parley project list", error, StringComparison.Ordinal);
        Assert.Contains("parley terminal", error, StringComparison.Ordinal);

        foreach (var action in new[] { "add", "set", "remove" })
        {
            Assert.DoesNotContain($"parley project {action}", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_destroy_flag_points_at_the_terminal_rather_than_at_another_command()
    {
        // **`--destroy` named `project remove`, which has itself moved.** A refusal still naming it
        // would send somebody to a command that refuses them in turn -- two hops to find out where
        // destroying a project actually happens, and the first hop confidently wrong.
        var (code, error) = ToolProcess.Run([], "registry", "--destroy");

        Assert.Equal(64, code);
        Assert.DoesNotContain("parley project remove", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_tells_a_person_to_run_what_was_removed()
    {
        // The sweep the sixteen-verb move needed, applied here: a machine with no projects is the
        // state somebody meets on their first install, and every sentence printed to them named
        // `project add` as the way on. All of them had to move together, or the tool would be
        // advertising a command it refuses.
        // `Reading` rather than `Run` for the listing, because what a person with no projects sees
        // is written to standard output -- "No projects configured", and then the sentence saying
        // where to go. The overload that drops the output would have checked the one half of this
        // that was never the problem.
        var listed = ToolProcess.Reading("project", "list");

        foreach (var said in new[]
        {
            listed.Output,
            listed.Error,
            ToolProcess.Run([], "project", "add", "orders").Error,
        })
        {
            foreach (var action in new[] { "add", "set", "remove" })
            {
                Assert.DoesNotContain($"parley project {action}", said, StringComparison.Ordinal);
            }
        }
    }
}
