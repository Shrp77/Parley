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
        // `parley console` still runs, and is documented in exactly one place as the old spelling
        // with the version it goes at. Anywhere else it would be an instruction rather than a note,
        // and an instruction to type a command that is being removed is worse than silence.
        var text = RepositoryFile.Read(file);
        var lines = text.Split('\n');

        var offering = lines
            .Where(line => line.Contains("parley console", StringComparison.Ordinal))
            .Where(line => !line.Contains("old spelling", StringComparison.Ordinal))
            .Where(line => !line.Contains("still works", StringComparison.Ordinal))
            .Where(line => !line.Contains("0.21.0", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        Assert.True(
            offering.Count == 0,
            $"{file} still offers the old command: " + string.Join(" | ", offering));
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
    public void The_old_flags_are_honoured_and_say_when_they_stop_being()
    {
        // A deprecation with no date is a kindness that becomes permanent. The version is in the
        // message a person sees rather than only in a document, because the person who needs it is
        // the one who just used the old spelling.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains("--with-console", program, StringComparison.Ordinal);
        Assert.Contains("removed at 0.21.0", program, StringComparison.Ordinal);
    }
}
