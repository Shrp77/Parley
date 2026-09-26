using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `autostart status` and `autostart remove` do not contradict each other, or the install.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by running the installers end to end on Linux (T107), in three consecutive lines of one
/// transcript.</b> `install.sh` reported "Wrote …/parley-broker.service, but could not enable it";
/// `parley autostart status` then said "Parley's broker starts at logon"; and
/// `parley autostart remove` said "Nothing to remove: Parley's broker is not registered to start at
/// logon here" — after deleting the unit file.
/// </para>
/// <para>
/// Two separate defects with one shape: a command answering a question it had not asked.
/// </para>
/// <para>
/// <b>`status` read only whether the unit file existed.</b> `install` writes the unit and then
/// enables it, and says so honestly when the enable fails — so the next command turned that into
/// good news. Two commands, one machine, opposite answers, and the one a person believes is the one
/// that sounds like success. It asks the supervisor now, and has a third answer for a machine that
/// cannot be asked, on the rule this repository already applies to a guard that fails open.
/// </para>
/// <para>
/// <b>`remove` returned whether `systemctl disable` succeeded</b>, so where systemctl fails or is
/// absent it deleted the unit file and reported that nothing had been there. An entry removed and
/// described as never installed is the worst of the three answers: the next thing somebody does is
/// go looking for what is still there. `RemoveLaunchAgentAsync`, forty lines below, has always said
/// `|| existed` — one of two sibling methods had the rule and the other did not.
/// </para>
/// <para>
/// <b>Read off the source, and the honest reason is that the machine running this suite has a real
/// autostart entry.</b> Driving `autostart remove` here would take down the developer's own logon
/// entry — which `reset.ps1` records happening once, to the machine that was writing it. Windows is
/// also the wrong platform to drive the systemd paths from. What can be checked without doing that
/// is that the decisions are the ones described.
/// </para>
/// </remarks>
public sealed class AutostartSaysWhatIsTrueTests
{
    private static string Source => RepositoryFile.Read("src/Parley.Cli/AutostartCommand.cs");

    [Fact]
    public void Removing_a_unit_file_counts_as_having_removed_something()
    {
        // The fix, and the shape of it: the file having been there is part of the answer, not just
        // whether the supervisor accepted the disable.
        var source = Source;

        Assert.Contains("return disabled.ExitCode == 0 || existed;", source, StringComparison.Ordinal);

        // And the sibling that always had it, so a future edit to one is visibly out of step.
        Assert.Contains("return removed.ExitCode == 0 || existed;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_asks_whether_the_entry_is_enabled_rather_than_only_present()
    {
        // `File.Exists` stays — it is the cheap first question and the right one for "is there an
        // entry at all". What must not happen is that answer alone becoming "starts at logon".
        var source = Source;

        Assert.Contains("EnabledAsync", source, StringComparison.Ordinal);
        Assert.Contains("is-enabled", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_has_a_third_answer_for_a_machine_it_cannot_ask()
    {
        // **The half that keeps the fix from being a new lie.** A machine with no systemd cannot
        // say whether a unit is enabled, and answering "not enabled" there would be as wrong as
        // answering "starts at logon" was — it would send somebody to re-run an install that had
        // already done everything it could.
        var source = Source;

        Assert.Contains("could not be checked on this machine", source, StringComparison.Ordinal);
        Assert.Contains("Task<bool?>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_still_asks_the_scheduler()
    {
        // Unaffected, and deliberately: `ScheduledTasks.Exists` asks the scheduler, which is the
        // real question on Windows — there is no separate enable step to disagree with. A fix that
        // routed Windows through the new path would add a check that cannot fail and a sentence
        // nobody there will ever see.
        var source = Source;

        Assert.Contains("ScheduledTasks.Exists(plan.Label)", source, StringComparison.Ordinal);
        Assert.Contains("plan.Supervisor == Supervisor.ScheduledTask", source, StringComparison.Ordinal);
    }
}
