using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That an install which must stop the broker says what stopping it will interrupt.
/// </summary>
/// <remarks>
/// <para>
/// SC-003 and FR-012. SPEC.md §12 says the node stopping tells every agent it started to quit, and
/// the process is what held everything each had learned. A question already drained into an
/// agent's inbox is marked delivered, so it sits in §4.2's four-hour Working window rather than
/// the fifteen-minute Delivery one — killing the agent that holds it leaves the exchange open and
/// owed with nothing running to answer, ending as <c>ghosted</c>, which §5.3 defines as "nobody
/// replied", recorded against an agent that was killed rather than one that ignored anything.
/// </para>
/// <para>
/// <b>The most important assertion here is the negative one.</b> Only the broker opens the store
/// (§4), and reading it directly for a progress message would break the boundary the whole
/// architecture is built on — for a warning. The scripts ask the broker.
/// </para>
/// </remarks>
public sealed class InFlightWarningTests
{
    private static string Windows => RepositoryFile.Read("install.ps1");

    private static string Unix => RepositoryFile.Read("install.sh");

    public static TheoryData<string> BothScripts => ["install.ps1", "install.sh"];

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_asks_the_broker_rather_than_reading_the_store(string script)
    {
        var text = RepositoryFile.Read(script);

        Assert.Contains("parley open", text, StringComparison.Ordinal);

        // The negative, and the reason this test exists. A script that opened the database would be
        // quicker and would break §4, which gives the store to one process.
        //
        // **Naming the store is not opening it, and a first draft confused the two.** These scripts
        // pass a path to `autostart install --db`, so `parley.db` appears in both and always will.
        // What must not appear is anything that reads it.
        // Named as the ways a script could actually open one, rather than as the word. Both scripts
        // say "SQLite" out loud -- one in its help, one in the sentence it prints while asking where
        // the store should go -- and a check that forbade the word would delete the explanation to
        // satisfy itself.
        var code = ExecutableLines(text);

        foreach (var opener in new[]
                 {
                     "sqlite3", "Invoke-Sqlite", "Microsoft.Data.Sqlite", "System.Data.SQLite",
                     "SQLiteConnection", "Get-Content",
                 })
        {
            Assert.DoesNotContain(opener, code, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void A_broker_that_cannot_be_asked_is_said_so_rather_than_read_as_quiet(string script)
    {
        // The failure mode this exists to prevent is silence that looks like an answer. "Could not
        // ask" and "nothing is in flight" are indistinguishable from the operator's side and lead
        // somewhere completely different, and this repository has one recorded instance of a guard
        // failing open without saying so.
        var text = RepositoryFile.Read(script);

        Assert.Contains("could not ask the broker", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not the same as nothing being in flight", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void The_warning_comes_before_anything_stops(string script)
    {
        // A warning after the fact is a report. The whole value is that somebody can press Ctrl+C.
        var text = RepositoryFile.Read(script);

        var asks = text.IndexOf("parley open", StringComparison.Ordinal);
        var stops = text.IndexOf("Stopping any running broker", StringComparison.Ordinal);

        Assert.True(asks > 0, $"{script} never asks what is open.");
        Assert.True(
            asks < stops,
            $"{script} stops the broker before asking what that would interrupt.");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void It_warns_rather_than_refuses(string script)
    {
        // -Force already means the decision belongs to the person making it, and blocking an
        // upgrade is not this script's job. Read as: the warning path does not exit.
        var text = RepositoryFile.Read(script);

        var asks = text.IndexOf("parley open", StringComparison.Ordinal);
        var stops = text.IndexOf("Stopping any running broker", StringComparison.Ordinal);
        var between = text[asks..stops];

        Assert.DoesNotContain("exit 1", between, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Error", between, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_stops_no_broker_asks_nothing()
    {
        // Nothing is going to be interrupted, so a warning would be noise on the exact run the
        // split exists to make quiet. Read positionally: the ask is inside the same branch as the
        // stop.
        var text = Windows;

        var decides = text.IndexOf("if ($replacingBroker) {", StringComparison.Ordinal);
        var asks = text.IndexOf("parley open", StringComparison.Ordinal);

        Assert.True(decides > 0, "install.ps1 no longer decides whether it is replacing the broker.");
        Assert.True(
            asks > decides,
            "install.ps1 asks what is open before deciding whether it will stop anything, so a "
            + "terminal-only install warns about exchanges it is not going to interrupt.");

        var shell = Unix;

        var shellDecides = shell.IndexOf("if [ \"$replacing_broker\" = \"1\" ]; then", StringComparison.Ordinal);
        var shellAsks = shell.IndexOf("parley open", StringComparison.Ordinal);

        Assert.True(shellDecides > 0, "install.sh no longer decides whether it is replacing the broker.");
        Assert.True(shellAsks > shellDecides, "install.sh asks before it decides.");
    }

    [Fact]
    public void The_command_it_asks_with_reports_open_exchanges_and_nothing_else()
    {
        // `parley open` is the same §10.2 conversations() reply both surfaces read, filtered to
        // what is open. It discloses nothing a surface does not, which is what makes it safe to
        // put on the command line at all.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains("case \"open\" when rest.Length == 0:", program, StringComparison.Ordinal);
        Assert.Contains("c.HasOpenExchange", program, StringComparison.Ordinal);
    }

    /// <summary>A script with its comment lines removed.</summary>
    /// <param name="text">The script.</param>
    /// <returns>Its executable lines.</returns>
    /// <remarks>
    /// The comments here explain at length why the store is not read, so a check reading the raw
    /// text would be measuring the explanation. PowerShell's block comment is why this is not
    /// enough on its own: a &lt;# ... #&gt; header spans many lines and none of them begins with a
    /// hash — which is why the check above names openers rather than forbidding a word.
    /// </remarks>
    private static string ExecutableLines(string text) =>
        string.Join(
            '\n',
            text.Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();

                    return !trimmed.StartsWith('#') && !trimmed.StartsWith("<#", StringComparison.Ordinal);
                }));
}
