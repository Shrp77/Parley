using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the entry a machine installs brings up the console with the broker.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §17 question 15 records the reason. An agent attached through somebody's own editor
/// window is the one attachment nothing can hand a turn to — the node gives turns only to
/// residents it started, and nothing can make a person's window take one. So a person takes part
/// through the console, and a console that had to be remembered separately would be the half of
/// a deployment that works for agents and not for people.
/// </para>
/// <para>
/// One entry rather than a second task, for the reason <see cref="AutostartPlan.Name"/> already
/// gives about the node: a second entry is a second thing to install, remove, and find already
/// removed.
/// </para>
/// </remarks>
public sealed class AutostartConsoleTests
{
    private const string Store = "/tmp/parley/parley.db";
    private const string Binary = "/usr/local/bin/parley";

    [Fact]
    public void The_entry_starts_a_console_when_one_is_asked_for()
    {
        var plan = AutostartPlan.Describe(
            Store, listenOn: null, executable: Binary, nodeConfig: null,
            consoleOn: "http://127.0.0.1:5280");

        Assert.Contains("--with-console", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5280", plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_address_is_written_in_even_when_it_is_the_default()
    {
        // `autostart status` prints these arguments, and "where is my console" answered by an
        // absence is the question this exists to stop being asked. A default left implicit is a
        // default somebody has to already know.
        var plan = AutostartPlan.Describe(
            Store, listenOn: null, executable: Binary, nodeConfig: null,
            consoleOn: Parley.Web.ConsoleHost.LoopbackAddress);

        Assert.Contains(
            Parley.Web.ConsoleHost.LoopbackAddress, plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_asked_for_no_console_installs_none()
    {
        var plan = AutostartPlan.Describe(
            Store, listenOn: null, executable: Binary, nodeConfig: null, consoleOn: null);

        Assert.DoesNotContain("--with-console", plan.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--console", plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_broker_the_node_and_the_console_share_one_entry()
    {
        // The property that makes the name still right. Three things start, and `remove` finds
        // all of them by the one handle `install` left behind.
        var plan = AutostartPlan.Describe(
            Store, listenOn: null, executable: Binary,
            nodeConfig: "/tmp/parley/node.json",
            consoleOn: Parley.Web.ConsoleHost.LoopbackAddress);

        Assert.Contains("serve", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains("--with-node", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains("--with-console", plan.Arguments, StringComparison.Ordinal);
        Assert.Equal("parley-broker", AutostartPlan.Name);
    }

    [Fact]
    public void A_console_address_survives_being_quoted_into_the_entry()
    {
        // The arguments are a command line a supervisor re-splits. An address that came back
        // split would install an entry that fails at logon, which is the failure nobody sees
        // until the machine is rebooted.
        var plan = AutostartPlan.Describe(
            Store, listenOn: null, executable: Binary, nodeConfig: null,
            consoleOn: "http://127.0.0.1:5280");

        var split = AutostartPlan.Split(plan.Arguments);

        var at = split.ToList().IndexOf("--console");

        Assert.True(at >= 0, "the console flag did not survive splitting: " + plan.Arguments);
        Assert.Equal("http://127.0.0.1:5280", split[at + 1]);
    }

    [Fact]
    public void A_flag_that_would_do_nothing_is_refused_rather_than_ignored()
    {
        // --with-console on `autostart install` reads as switching something on and would change
        // nothing, because the console is already the default there. A flag accepted and ignored
        // is worse than one refused: the person who typed it believes it did something.
        //
        // Asserted against the source because the guard is in top-level statements with no seam
        // to call. The behaviour itself was driven at the command line; this stops the guard
        // being deleted quietly.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains(
            "if (hostConsole && verb != \"serve\")", program, StringComparison.Ordinal);

        Assert.Contains(
            "--console and --no-console are for `parley serve`", program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_console_cannot_take_the_broker_down_with_it()
    {
        // The containment that makes hosting it in the broker's process defensible at all. Its
        // likeliest failure is not a fault but a port already in use, and a broker that refused
        // to start because a web page could not bind would take a whole deployment down for the
        // one part of it no agent uses.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        //
        // Asserted on what the block does rather than on what it says. An earlier version pinned
        // the sentence, and rewording it broke a test that had no opinion about the wording —
        // which teaches the next person to loosen the assertion rather than to keep the guard.
        var start = program.IndexOf("async Task<int> HostingConsole", StringComparison.Ordinal);

        Assert.True(start > 0, "the console is no longer hosted from Program.cs");

        var rest = program[start..];
        var next = rest.IndexOf("        async Task<int> Hosting", 10, StringComparison.Ordinal);
        var hosting = next > 0 ? rest[..next] : rest;

        Assert.Contains("catch (Exception failure)", hosting, StringComparison.Ordinal);

        // Handled, not propagated: a rethrow here is the broker going down with the web page.
        Assert.DoesNotContain("throw;", hosting, StringComparison.Ordinal);
        Assert.Contains("return Unavailable;", hosting, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Neither_a_console_nor_a_node_is_refused_across_several_projects()
    {
        // Found by running it, twice over. A console reads this machine's manifest and is at its
        // best across several projects, and it was once refused because it shared a hook with the
        // node. The node was then refused across several projects on its own, on the grounds
        // that a machine has one identity — until an autostart entry serving the manifest had
        // nowhere to put the node at all. It is hosted per catalogued project now, so neither
        // hook may throw at a person. Cancelled up front so nothing binds and the test needs no
        // sockets: anything that is not an ArgumentException got past the guard, which is the
        // whole claim.
        var projects = new[]
        {
            new ProjectSettings(
                ProjectName.Create("alpha"), "alpha.db", "http://127.0.0.1:0", null, null),
            new ProjectSettings(
                ProjectName.Create("beta"), "beta.db", "http://127.0.0.1:0", null, null)
        };

        using var stopping = new CancellationTokenSource();

        await stopping.CancelAsync();

        var node = await Record.ExceptionAsync(() => ServeCommand.RunAsync(
            projects, null, TextWriter.Null, TextWriter.Null,
            nodeFor: (_, _, _, _) => Task.FromResult(0),
            cancellationToken: stopping.Token));

        Assert.IsNotType<ArgumentException>(node);

        var console = await Record.ExceptionAsync(() => ServeCommand.RunAsync(
            projects, null, TextWriter.Null, TextWriter.Null,
            alongside: (_, _, _) => Task.FromResult(0),
            cancellationToken: stopping.Token));

        Assert.IsNotType<ArgumentException>(console);
    }

    [Fact]
    public void The_help_says_the_console_is_installed_by_default()
    {
        // Because "by default" is the whole of the decision, and a default nobody documents is
        // a surprise rather than a convenience.
        var help = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains("--no-console", help, StringComparison.Ordinal);
        Assert.Contains(
            "The console is installed by default", help, StringComparison.Ordinal);
    }
}
