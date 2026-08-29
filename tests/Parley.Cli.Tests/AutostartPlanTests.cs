using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What autostart would install, checked without installing anything.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because the hand-written version of this command was wrong, and wrong in
/// the way command lines usually are. It wrapped an unspaced path in escaped quotes, which
/// <c>schtasks</c> refused with "Invalid argument/option" followed by the entire command line —
/// a message that reads as though the verb were wrong rather than the quoting.
/// </para>
/// <para>
/// So the composition is a pure function and this checks its output. Registering a task, writing
/// a unit file and loading a launch agent are all things a test cannot do to a developer's
/// machine without leaving something behind; rendering the text they would be given is both
/// testable and where the defects are.
/// </para>
/// </remarks>
public sealed class AutostartPlanTests
{
    private const string Store = "/tmp/parley/parley.db";

    private static AutostartPlan Plan(string? listenOn = null) =>
        AutostartPlan.Describe(Store, listenOn, executable: "/usr/local/bin/parley");

    [Fact]
    public void A_path_without_spaces_is_not_quoted()
    {
        // The defect this whole file is named after. Quoting is applied because a value needs it,
        // never because it might: schtasks parses the /TR value itself, and quotes around
        // something that did not need them make it reject the argument entirely.
        var command = Plan().CommandLine();

        Assert.DoesNotContain("\"", command, StringComparison.Ordinal);
        Assert.Contains("serve --db", command, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_spaces_is_quoted()
    {
        // The other half, and the reason quoting cannot simply be dropped. "C:/Program
        // Files/..." unquoted becomes two arguments, and the broker is handed a database path
        // that stops at the space — which it would then create, leaving an empty store beside a
        // full one.
        var plan = AutostartPlan.Describe(
            "C:/Some Folder/parley.db", executable: "C:/Program Files/parley/parley.exe");

        var command = plan.CommandLine();

        Assert.Contains("\"C:/Program Files/parley/parley.exe\"", command, StringComparison.Ordinal);
        // Through GetFullPath, because that is what the plan does and Windows normalises the
        // separators on the way. A literal here would pin one platform's spelling of a path.
        Assert.Contains(
            $"\"{Path.GetFullPath("C:/Some Folder/parley.db")}\"",
            command,
            StringComparison.Ordinal);

        // And it survives being taken apart again, which is what launchd needs.
        Assert.Contains(
            AutostartPlan.Split(plan.Arguments),
            part => part.Contains("Some Folder", StringComparison.Ordinal));
    }

    [Fact]
    public void The_store_is_made_absolute()
    {
        // A logon task has no working directory worth relying on, so a relative path installed
        // today names a different file tomorrow. The failure is quiet: a broker that comes up
        // healthy against an empty store, which looks exactly like every agent having lost its
        // registration.
        var plan = AutostartPlan.Describe("parley.db", executable: "parley");

        Assert.DoesNotContain("--db parley.db", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath("parley.db"), plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_sits_beside_the_store()
    {
        // A supervisor's stdout goes nowhere by default, so a broker that failed to start would
        // fail silently. Beside the store because that is the directory this deployment already
        // owns, and the one somebody hunting for the broker will already have open.
        var plan = Plan();

        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath(Store)),
            Path.GetDirectoryName(plan.LogPath));
    }

    [Fact]
    public void An_endpoint_is_carried_when_one_was_chosen()
    {
        Assert.DoesNotContain("--listen", Plan().Arguments, StringComparison.Ordinal);

        Assert.Contains(
            "--listen http://127.0.0.1:9999",
            Plan("http://127.0.0.1:9999").Arguments,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_systemd_unit_restarts_a_broker_that_died_and_not_one_that_was_stopped()
    {
        var unit = Plan().SystemdUnit();

        // on-failure, not always. A broker that exited zero was stopped on purpose, and
        // restarting it would make stopping it impossible without disabling the unit as well.
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart=always", unit, StringComparison.Ordinal);

        // A user unit. The system equivalent starts before anybody logs in and needs root to
        // install, and the broker needs neither.
        Assert.Contains("WantedBy=default.target", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("multi-user.target", unit, StringComparison.Ordinal);

        Assert.Contains("/usr/local/bin/parley serve --db", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void The_launch_agent_lists_its_arguments_separately()
    {
        // launchd takes an array rather than a command line, so a path with a space in it has to
        // survive being split. Checked as XML elements rather than as a substring, because a
        // command line that happened to appear inside one <string> would pass a looser check and
        // fail at load.
        var plan = AutostartPlan.Describe(
            "/Users/someone/Library/Application Support/parley.db", executable: "/usr/local/bin/parley");

        var plist = plan.LaunchAgentPlist();

        Assert.Contains("<string>/usr/local/bin/parley</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>serve</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>--db</string>", plist, StringComparison.Ordinal);
        Assert.Contains(
            $"<string>{Path.GetFullPath("/Users/someone/Library/Application Support/parley.db")}</string>",
            plist,
            StringComparison.Ordinal);

        // SuccessfulExit false, matching the systemd unit: a broker somebody stopped stays
        // stopped.
        Assert.Contains("<key>SuccessfulExit</key>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_with_markup_in_it_cannot_break_the_plist()
    {
        // The store path reaches the plist as XML content, and a path is user input in every
        // sense that matters -- somebody's directory can contain an ampersand. Unescaped it
        // produces a file launchd refuses to parse, and the symptom is an agent that never
        // loads with nothing anywhere saying why.
        var plan = AutostartPlan.Describe(
            "/tmp/odd & awkward/parley.db", executable: "/usr/local/bin/parley");

        var plist = plan.LaunchAgentPlist();

        Assert.Contains("&amp;", plist, StringComparison.Ordinal);

        // The unescaped ampersand, which is the thing launchd chokes on. This asserted the absence
        // of "awkward/parley.db" instead — a substring that survives escaping intact and is
        // *supposed* to, so the check only ever passed because Windows had rewritten the
        // separator to a backslash by the time it ran. Vacuous on one platform and wrong on the
        // other two, which is the worst of both: it was green everywhere anybody looked.
        Assert.DoesNotContain("odd & awkward", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void This_platform_has_a_supervisor()
    {
        // Guards the whole suite against being vacuous on a platform nothing supports: every
        // check above would still pass against a plan that installs nothing.
        Assert.NotEqual(Supervisor.Unsupported, AutostartPlan.Detect());
    }

    [Fact]
    public void The_scheduled_task_survives_a_laptop()
    {
        // Three defaults would have killed a broker on a machine that moves, and none of them
        // would have looked like the task's fault. A scheduled task will not start on battery
        // and stops when the machine goes onto battery, both by default -- so a broker installed
        // at a desk would vanish on a train, and every agent would report a peer it could not
        // reach. The third is the execution time limit, which defaults to three days: long
        // enough that nobody connects the broker dying to the thing that started it.
        var xml = AutostartPlan
            .Describe("/tmp/parley/parley.db", executable: "/usr/local/bin/parley")
            .ScheduledTaskXml(@"EXAMPLE\someone");

        Assert.Contains(
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
            xml, StringComparison.Ordinal);

        Assert.Contains(
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
            xml, StringComparison.Ordinal);

        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scheduled_task_asks_for_no_privilege_it_does_not_need()
    {
        // A broker binds a loopback port and writes under the user's own profile. Registering it
        // to run with the highest available privileges would mean it ran elevated for ever
        // after, for nothing -- and would make the install itself require a consent prompt.
        var xml = AutostartPlan
            .Describe("/tmp/parley/parley.db", executable: "/usr/local/bin/parley")
            .ScheduledTaskXml(@"EXAMPLE\someone");

        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("HighestAvailable", xml, StringComparison.Ordinal);

        // InteractiveToken is what lets this register without a stored password.
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_supervisor_is_told_where_to_put_the_output()
    {
        // A supervisor runs a program, not a command line, so a broker started at logon has
        // nowhere for its output to go unless it writes somewhere itself. Without this a broker
        // that failed to start under a scheduled task would fail silently, and the symptom would
        // be agents refusing to launch a week later.
        var plan = AutostartPlan.Describe("/tmp/parley/parley.db", executable: "/usr/local/bin/parley");

        Assert.Contains("--log", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains(plan.LogPath, plan.Arguments, StringComparison.Ordinal);
    }
}
