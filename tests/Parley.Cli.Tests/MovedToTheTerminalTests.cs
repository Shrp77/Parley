using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The sixteen operator operations that left the command line, and what a person who types one gets.
/// </summary>
/// <remarks>
/// <para>
/// The terminal is the only surface where these can sensibly be
/// offered, so it is where they live. The command line keeps what brings a machine up and what a
/// program calls.
/// </para>
/// <para>
/// <b>Driven through the real binary, because the refusal is in the entry point.</b> An in-process
/// call never reaches it — the same reason <c>FilesCommand.OwnOptions</c> is tested this way, after
/// half of <c>parley files</c> turned out to be unreachable from a shell while every in-process test
/// passed.
/// </para>
/// </remarks>
public sealed class MovedToTheTerminalTests
{
    /// <summary>Every verb that moved.</summary>
    /// <remarks>
    /// Spelled out rather than read back out of the source. A test that derived this list from the
    /// same file it is checking would pass against a build that had quietly dropped one, which is
    /// the whole failure mode a list like this exists to catch.
    /// </remarks>
    public static TheoryData<string> Moved =>
    [
        "retire", "reinstate", "purge", "export", "force-conclude", "prune", "start",
        "archive", "unarchive", "destroy", "refusals", "clarifications", "results", "flags",
    ];

    [Theory]
    [MemberData(nameof(Moved))]
    public void A_moved_command_exits_sixty_four_and_names_the_surface(string verb)
    {
        var (code, error) = ToolProcess.Run([], verb);

        // EX_USAGE, so a script detects it. Zero would read as "it worked and printed nothing",
        // which is the worst answer a removed command can give a pipeline.
        Assert.Equal(64, code);
        Assert.Matches(@"(?m)^\s+parley\s*$", error);
    }

    [Theory]
    [MemberData(nameof(Moved))]
    public void A_moved_command_answers_without_a_broker(string verb)
    {
        // **The machine somebody types this on is the one with nothing running.** A refusal that
        // first resolved an endpoint and opened a channel would report "no broker is listening"
        // — the wrong problem, on exactly the run where the right one is that the command has gone.
        var (_, error) = ToolProcess.Run(
            [("PARLEY_BROKER", "http://127.0.0.1:1")], verb);

        Assert.DoesNotContain("No broker is listening", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Moved))]
    public void A_moved_command_never_tells_somebody_to_run_it(string verb)
    {
        // Two things at once. The obvious one: a refusal that said "`parley export` moved" would be
        // telling a person to type the command it is refusing.
        //
        // The other is a build constraint. `PluginTests` extracts every `parley <word>` from the
        // whole of `Program.cs` — comments and strings included — and demands set equality with the
        // operator skill's list. A stub naming its own verb after the word `parley` puts that verb
        // back into the set and fails the build somewhere else entirely, with a message about a
        // skill file. Four comments in `Program.cs` had to be reworded for exactly this reason.
        var (_, error) = ToolProcess.Run([], verb);

        Assert.DoesNotContain($"parley {verb}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_commands_a_script_calls_are_not_among_them()
    {
        // **A guard rather than a promise.** This repository's own installers call
        // `open` and `registry`, and the plugin's hook calls `waiting` — so those three going the
        // same way as the sixteen would break the tooling that installs Parley, which is the one
        // failure nobody would see until they reinstalled.
        foreach (var kept in new[] { "open", "registry", "waiting" })
        {
            var (code, error) = ToolProcess.Run([], kept);

            Assert.True(
                code != 64 || !error.Contains("is not a command here.", StringComparison.Ordinal),
                $"'{kept}' was removed, and this repository's own scripts call it");
        }
    }
}
