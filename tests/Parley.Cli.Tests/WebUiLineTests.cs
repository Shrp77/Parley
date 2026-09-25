using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `autostart install` and `autostart status` say where the web UI listens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by the owner on the first install that produced no console window.</b> The command
/// printed what it runs, where it logs, what it serves and what it hosts — and the address of the
/// page a person actually opens was in none of them. It was in the command line, as
/// <c>--web-ui &lt;url&gt;</c> at the end of a line nobody reads to the end of.
/// </para>
/// <para>
/// <b>One writer for two commands.</b> Somebody runs `install` once and `status` whenever they have
/// forgotten what is installed, so both are places this question is asked — and this repository's
/// recurring defect is one rule with two implementations that agree until one of them is edited.
/// </para>
/// </remarks>
public sealed class WebUiLineTests
{
    private const string Loopback = "http://127.0.0.1:5280";

    [Fact]
    public void The_address_is_on_a_line_of_its_own()
    {
        Assert.Equal($"  web UI: {Loopback}", AutostartCommand.WebUiLine(Loopback, "none"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_web_UI_is_said_rather_than_left_out(string? address)
    {
        // The `hosts:` line above this one already settles the question: an absence reads exactly
        // like a line somebody missed, and "why does that address not open" is then answered by
        // nothing at all.
        //
        // Absent is null or empty, which is the test `AutostartPlan` applies to the same value when
        // it decides whether to write `--with-web-ui` at all. Two different ideas of "no address"
        // would be a line saying none over an entry that starts one, or the reverse.
        var line = AutostartCommand.WebUiLine(address, "none, because --no-web-ui was given.");

        Assert.StartsWith("  web UI: ", line, StringComparison.Ordinal);
        Assert.Contains("none", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_is_the_same_whichever_state_the_entry_is_in()
    {
        // What somebody scans an installer's output for is the words, so the words cannot depend on
        // the answer. The two commands differ in what they say *after* the colon -- `install` knows
        // a flag was given and `status` only sees an entry -- and that is the whole of the licence
        // the caller has.
        Assert.StartsWith("  web UI: ", AutostartCommand.WebUiLine(Loopback, "none"), StringComparison.Ordinal);
        Assert.StartsWith("  web UI: ", AutostartCommand.WebUiLine(null, "none"), StringComparison.Ordinal);
    }

    [Fact]
    public void It_carries_no_flag_that_install_ps1_scrapes_for()
    {
        // `install.ps1` reads this command's output back and takes the address out of the `runs:`
        // line by matching `--web-ui`. A second line carrying that spelling would hand the scrape a
        // second candidate -- and it takes the first, so which one won would depend on the order
        // these lines are printed in. A guard here rather than in the script, because this is the
        // side that would change.
        Assert.DoesNotContain("--web-ui", AutostartCommand.WebUiLine(Loopback, "none"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--web-ui",
            AutostartCommand.WebUiLine(null, "none. `parley autostart install` registers one by default."),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Both_commands_use_it()
    {
        // The point of the helper is that there is one spelling, and a caller that formatted its own
        // line would leave that true only by habit. Two calls: one in the install path, one in the
        // status path.
        var source = RepositoryFile.Read("src/Parley.Cli/AutostartCommand.cs");
        var calls = 0;
        var at = source.IndexOf("WebUiLine(", StringComparison.Ordinal);

        while (at >= 0)
        {
            calls++;
            at = source.IndexOf("WebUiLine(", at + 1, StringComparison.Ordinal);
        }

        // Three: the declaration and the two callers.
        Assert.True(calls >= 3, $"AutostartCommand.cs names WebUiLine {calls} time(s), so one of the two commands is formatting its own line");
    }
}
