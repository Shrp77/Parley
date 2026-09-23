using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That both install scripts can install a machine's surfaces without installing all of them.
/// </summary>
/// <remarks>
/// <para>
/// The surfaces became separate tools so that changing one would not stop the broker. Being
/// separate makes them skippable, and skippable is what a machine running only agents wants —
/// but a skip has a cost the scripts must name: a surface not installed today is not removed
/// either, so it stays at whatever version it had, speaking SPEC.md §10.2 to a broker that has
/// moved on.
/// </para>
/// <para>
/// Checked as text, like every other check in <c>InstallScriptTests</c>, and for the same reason:
/// running these scripts installs global tools on whoever's machine runs the suite. What the text
/// can prove is that the selection is consulted everywhere it has to be, and — the more valuable
/// half — that it is <b>not</b> consulted where it must not be.
/// </para>
/// </remarks>
public sealed class InstallSurfacesTests
{
    private static string Windows => RepositoryFile.Read("install.ps1");

    private static string Unix => RepositoryFile.Read("install.sh");

    public static TheoryData<string> BothScripts => ["install.ps1", "install.sh"];

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_installs_the_terminal_as_a_tool_of_its_own(string script)
    {
        var text = RepositoryFile.Read(script);

        Assert.Contains("Parley.Terminal", text, StringComparison.Ordinal);
        Assert.Contains("parley-terminal", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_takes_the_four_surface_sets_and_refuses_a_fifth(string script)
    {
        var text = RepositoryFile.Read(script);

        foreach (var choice in new[] { "terminal", "web-ui", "both", "none" })
        {
            Assert.Contains(choice, text, StringComparison.Ordinal);
        }

        // A fifth value must be an error rather than silently meaning none, which would install
        // the broker and quietly skip both surfaces on a typo.
        Assert.Contains("takes terminal, web-ui, both or none", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_scripts_default_differently_and_each_says_why()
    {
        // Windows has somebody at a screen; the machine install.sh runs on usually does not, and
        // has no browser either -- but it can serve a web UI over a tunnel, so the default there
        // is the web UI rather than nothing.
        Assert.Contains("'both' } else { 'web-ui' }", Windows, StringComparison.Ordinal);
        Assert.Contains("surfaces=\"web-ui\"", Unix, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void A_skipped_surface_that_is_already_installed_is_named_rather_than_passed_over(
        string script)
    {
        // The cost of the flag, and the reason it has to be said out loud. §10.2 is a versioned
        // contract, so a surface left behind is a client of a broker it may no longer agree with
        // -- and nothing else on the machine will ever mention it.
        var text = RepositoryFile.Read(script);

        Assert.Contains("left as it was", text, StringComparison.Ordinal);
        Assert.Contains("was not updated to", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_removes_every_tool_rather_than_the_selected_ones()
    {
        // **The check that matters most here, and it is a negative one.** An uninstall that
        // honoured the selection would leave behind a tool that nothing installs and nothing
        // removes: the next run skips it because it was not selected, and the uninstall skips it
        // for the same reason. It would be on the machine for ever.
        //
        // Read as: the uninstall block names no selection helper at all.
        var windows = Windows;
        var block = windows[windows.IndexOf("if ($Uninstall) {", StringComparison.Ordinal)..];

        block = block[..block.IndexOf("# ------------------------------------------------------------ prerequisites --",
            StringComparison.Ordinal)];

        Assert.DoesNotContain("Wanted", block, StringComparison.Ordinal);
        Assert.DoesNotContain("$Surfaces", block, StringComparison.Ordinal);

        var unix = Unix;
        var shell = unix[unix.IndexOf("if [ \"$uninstall\" = \"1\" ]; then", StringComparison.Ordinal)..];

        shell = shell[..shell.IndexOf("# ------------------------------------------------------------ prerequisites --",
            StringComparison.Ordinal)];

        Assert.DoesNotContain("is_wanted", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void The_holder_check_covers_a_running_terminal_and_only_what_is_being_replaced()
    {
        // Two properties in one place because they are the same edit. A terminal holds its own
        // executable exactly as an editor's adapter does, so it must be caught; and a surface this
        // run is skipping is not going to be unlinked, so it must not be — refusing to install the
        // broker because somebody has a terminal open would make the flag useless on the machine
        // most likely to want it.
        Assert.Contains("Wanted $_", Windows, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Q leaves it", Windows, StringComparison.Ordinal);
        Assert.Contains("is_wanted \"$i\" || continue", Unix, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void A_run_that_does_not_replace_the_broker_does_not_stop_it(string script)
    {
        // **SC-001, and it is one branch in each script.** A run that replaces only the terminal
        // unlinks a file the broker never opened, so there is nothing to stop. Without this branch
        // the feature is theory: the tools are separate, and installing either one still ends every
        // resident agent on the machine.
        var text = RepositoryFile.Read(script);

        Assert.Contains("Leaving the broker alone", text, StringComparison.Ordinal);

        // The stop is inside a conditional rather than unconditional above it. Read positionally,
        // because a branch that exists and is not reached is exactly what this would otherwise miss.
        var decides = text.IndexOf(
            script.EndsWith(".ps1", StringComparison.Ordinal) ? "$replacingBroker =" : "replacing_broker=0",
            StringComparison.Ordinal);

        var stops = text.IndexOf("Stopping any running broker", StringComparison.Ordinal);

        Assert.True(decides > 0, $"{script} never decides whether it is replacing the broker.");
        Assert.True(
            decides < stops,
            $"{script} stops the broker before deciding whether it needs to, which is the "
            + "unconditional stop this feature exists to remove.");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void A_mistyped_tool_name_is_refused_rather_than_selecting_nothing(string script)
    {
        // The failure shape every other guard in these scripts exists to prevent: a selection that
        // matches nothing packs nothing, installs nothing, and reports success.
        var text = RepositoryFile.Read(script);

        Assert.Contains("names no such tool", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void A_run_that_does_not_replace_the_broker_registers_no_autostart_entry(string script)
    {
        // **The half of SC-001 that is easy to miss, and it was missed here first.** Not stopping
        // the broker is worthless if the script re-registers the autostart entry afterwards:
        // `autostart install` replaces the scheduled task, which restarts the process, which ends
        // every resident agent anyway -- the stop avoided and the cost paid three hundred lines
        // later. Found by reading the script's tail before running it on a live deployment.
        var text = RepositoryFile.Read(script);

        var decides = text.IndexOf(
            script.EndsWith(".ps1", StringComparison.Ordinal) ? "if (-not $replacingBroker)" : "if [ \"$replacing_broker\" != \"1\" ]",
            StringComparison.Ordinal);

        var registers = text.IndexOf("autostart install", StringComparison.Ordinal);

        while (registers >= 0 && registers < decides)
        {
            // The first mentions are the comment explaining what the entry is called.
            registers = text.IndexOf("autostart install", registers + 1, StringComparison.Ordinal);
        }

        Assert.True(decides > 0, $"{script} never guards its broker section.");
        Assert.True(
            decides < registers,
            $"{script} registers an autostart entry before deciding whether it is replacing the "
            + "broker, so a surface-only run restarts it anyway.");
    }
}
