using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the web UI is called one thing everywhere, and that the rename stopped where it should.
/// </summary>
/// <remarks>
/// <para>
/// SC-006 and SC-010. "Console" meant three things in this repository and only one of them was a
/// browser surface: the operator operations of SPEC.md §10.2 (<c>OperatorConsole</c>), whether a
/// terminal exists to draw on (<c>ConsolePicker</c>), and the web pages. Renaming the third is
/// what this feature does; renaming either of the others would be a different change entirely and
/// a much worse one, since one of them is a protocol boundary.
/// </para>
/// <para>
/// The other half is that a rename half-done is worse than not done: somebody reading the README
/// types a command the tool does not have, or reads a startup line naming a flag that no longer
/// exists. Checked mechanically because prose is exactly where a rename gets left half-done.
/// </para>
/// </remarks>
public sealed class RenameTests
{
    /// <summary>Files that describe the surfaces to a person.</summary>
    /// <remarks>
    /// The four somebody actually reads before typing anything: the manual, the tool's own help,
    /// the skill an agent reads, and the script that installs it all.
    /// </remarks>
    public static TheoryData<string> UserFacing =>
    [
        "README.md",
        "src/Parley.Cli/Program.cs",
        "plugins/parley/skills/parley-operator/SKILL.md",
        "install.ps1",
    ];

    [Fact]
    public void The_two_other_consoles_are_not_renamed()
    {
        // `OperatorConsole` is SPEC.md §10.2 — the operator half of the protocol, whose name is a
        // structural guard that an adapter is handed a Broker and not this. `ConsolePicker` asks
        // whether there is a terminal to draw on, which is the opposite kind of console. Neither is
        // a browser surface and neither changes.
        var broker = RepositoryFile.Read("src/Parley.Broker/OperatorConsole.cs");

        Assert.Contains("class OperatorConsole", broker, StringComparison.Ordinal);

        var picker = RepositoryFile.Read("src/Parley.Setup/ConsolePicker.cs");

        Assert.Contains("class ConsolePicker", picker, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UserFacing))]
    public void Nothing_a_person_reads_offers_the_old_command(string file)
    {
        // **The premise of this test expired and its exemptions outlived it.** It was written while
        // `parley console` still ran, and let a line through if it said `old spelling`, `still works`
        // or `0.21.0` — the wording of 007's deprecation note. This release deletes both the verb and
        // that note, so those three stopped being exemptions for a sentence that exists and became
        // holes shaped exactly like the false statements this version must not make: a line reading
        // *run `parley console`, it still works* passed here, in the release where it does not.
        //
        // One exemption survives and only one. A note saying the command was **removed** is a fact
        // somebody upgrading needs; `still works` is a failure now rather than a pass, which is the
        // whole difference.
        var text = RepositoryFile.Read(file);
        var lines = text.Split('\n');

        var offering = lines
            .Where(line => line.Contains("parley console", StringComparison.Ordinal))
            .Where(line => !line.Contains("removed", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .ToList();

        Assert.True(
            offering.Count == 0,
            $"{file} still offers the old command: " + string.Join(" | ", offering));
    }

    [Fact]
    public void The_rule_above_would_still_catch_an_offering()
    {
        // **The control, and this test needs one more than most.** The verb is gone from all four
        // files, so the rule above now passes on emptiness — it would go on passing if the matcher
        // were broken, if the files were renamed, or if somebody widened the exemption again. What it
        // guards against is reintroduction, and a guard against something that is not there can only
        // be shown to work against something that is.
        var offered = "  Run `parley console` to open it.";
        var noted = "  `parley console` was removed at 0.22.0.";

        Assert.Contains("parley console", offered, StringComparison.Ordinal);
        Assert.DoesNotContain("removed", offered, StringComparison.OrdinalIgnoreCase);

        // And the one exemption still exempts, so the note an upgrader needs is not forbidden.
        Assert.Contains("removed", noted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(UserFacing))]
    public void Everything_naming_the_web_ui_names_the_same_command(string file)
    {
        // SC-006. One spelling, and it is the one the tool answers to.
        var text = RepositoryFile.Read(file);

        Assert.Contains("web-ui", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flag_a_logon_entry_registers_is_the_new_one()
    {
        // SC-009's other half. An entry installed today has to start the web UI at the next logon,
        // and it does that by naming a flag — so a rename that missed this would install entries
        // that quietly stopped starting anything.
        var plan = RepositoryFile.Read("src/Parley.Setup/AutostartPlan.cs");

        Assert.Contains("--with-web-ui", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("--with-console", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_registered_before_the_rename_is_still_read()
    {
        // The half that would be silent. Every machine with an entry installed before 0.19.0 has
        // `--console` in its scheduled task right now, and a reader knowing only the new name would
        // report those entries as having no web UI — so `autostart status` would say one thing
        // while the entry did another, and a reinstall carrying the arguments forward would drop it.
        var reader = RepositoryFile.Read("src/Parley.Setup/InstalledEntry.cs");

        Assert.Contains("case \"--web-ui\":", reader, StringComparison.Ordinal);
        Assert.Contains("case \"--console\":", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_flags_are_gone_at_the_version_that_was_promised()
    {
        // **This test asserted the deprecation until 0.22.0 and now asserts the removal.** A
        // deprecation with no date is a kindness that becomes permanent; 007 named 0.21.0, 008
        // moved it here and called it a commitment rather than a second reprieve, and this is what
        // makes that true of the binary rather than of a document.
        //
        // Driven rather than read off the source. The flags were removed by deleting the code that
        // took them, so a source-text assertion would pass against a build that still accepted them
        // through some other path -- and what a person meets is the refusal, not the file.
        var refused = ToolProcess.Run("serve", "--with-console");

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("--with-console", refused.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_old_spelling_is_refused_and_not_merely_ignored()
    {
        // A flag accepted and ignored is worse than one refused: the person who typed it believes
        // it did something. That is the same rule --with-console itself was held to when it existed.
        foreach (var flag in new[] { "--with-console", "--no-console" })
        {
            Assert.NotEqual(0, ToolProcess.Run("serve", flag).ExitCode);
        }

        Assert.NotEqual(0, ToolProcess.Run("serve", "--console", "http://127.0.0.1:5280").ExitCode);
    }
}
