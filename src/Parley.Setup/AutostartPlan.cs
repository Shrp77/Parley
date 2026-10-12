using System.Globalization;
using System.Security;

namespace Parley.Setup;

/// <summary>
/// Which supervisor a platform uses to start a broker when somebody logs in.
/// </summary>
public enum Supervisor
{
    /// <summary>Nothing here knows how to do it.</summary>
    Unsupported = 0,

    /// <summary>A Windows scheduled task, triggered at logon.</summary>
    ScheduledTask,

    /// <summary>A systemd user unit.</summary>
    SystemdUser,

    /// <summary>A launchd agent in the user's own LaunchAgents.</summary>
    LaunchAgent
}

/// <summary>
/// What installing autostart would do, worked out before anything is done.
/// </summary>
/// <param name="Supervisor">Which supervisor this platform uses.</param>
/// <param name="Label">What the installed thing is called.</param>
/// <param name="Executable">The broker binary to run.</param>
/// <param name="Arguments">What to pass it.</param>
/// <param name="LogPath">Where its output goes.</param>
/// <param name="UnitPath">The file to write, for platforms that install a file.</param>
/// <remarks>
/// <para>
/// Separated from the doing so that the hard part can be tested. Nothing here registers
/// anything: it renders a task definition, a unit file or a property list, and every defect this
/// feature is likely to have is a quoting or path defect visible in that text. A design that
/// composed and executed in one step could only be tested by installing something.
/// </para>
/// <para>
/// <b>Everything is user-level, on every platform, and that is the decision rather than a
/// limitation.</b> A broker binds a loopback port and writes to a file under the user's own
/// profile; it needs no privilege it does not already have, and asking for some would mean
/// every future start ran elevated. The consequence is that it starts at logon and stops at
/// logout, which is right for a machine somebody works at and wrong for a server nobody logs
/// into — see the remarks on <see cref="Describe"/>.
/// </para>
/// </remarks>
public sealed record AutostartPlan(
    Supervisor Supervisor,
    string Label,
    string Executable,
    string Arguments,
    string LogPath,
    string? UnitPath)
{
    /// <summary>What the installed thing is called, on every platform.</summary>
    /// <remarks>
    /// <para>
    /// One name, so that finding it does not depend on knowing which platform installed it.
    /// Recognisable in a task list without being so generic that it collides with something
    /// else somebody installed.
    /// </para>
    /// <para>
    /// <b>One entry, even though it now starts three things.</b> The process it launches hosts
    /// the broker, this machine's node, and the operator's console, so a deployment needs a
    /// single supervisor entry rather than one per part — which is what further entries would
    /// have been: more things to install, remove, and find already removed. The name did not
    /// change with the scope, because it is the handle by which <c>remove</c> finds what
    /// <c>install</c> left behind, and a rename would strand every entry installed by an earlier
    /// build.
    /// </para>
    /// </remarks>
    public const string Name = "parley-broker";

    /// <summary>
    /// Works out what installing would do on this machine.
    /// </summary>
    /// <param name="root">Where this machine keeps its Parley files.</param>
    /// <param name="project">
    /// The one project this entry should serve, or null to serve every project on the machine.
    /// </param>
    /// <param name="executable">The broker binary, or null to use the running one.</param>
    /// <param name="nodeConfig">
    /// The node catalogue to host this machine's agents from, or null to install a broker that
    /// hosts no node.
    /// </param>
    /// <param name="consoleOn">
    /// Where the operator's console should listen, or null to install a broker that hosts none.
    /// </param>
    /// <param name="hidden">
    /// Whether the broker should let go of its console window. A logon entry starts a console
    /// program in the person's own session, so without this one appears on their desktop every time
    /// they log in.
    /// </param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// The database path is required and is resolved to an absolute one. A logon task has no
    /// working directory worth relying on, so a relative path installed today would name a
    /// different file tomorrow — and the failure is a broker that comes up healthy against an
    /// empty store, which reads as every agent having lost its registration.
    /// </para>
    /// <para>
    /// The executable defaults to the running process rather than to whatever is on PATH at
    /// logon. A logon task runs with a PATH the user did not choose and may not include the
    /// .NET tools directory; more to the point, the broker somebody is installing is the one
    /// they just ran.
    /// </para>
    /// </remarks>
    public static AutostartPlan Describe(
        string root,
        string? project = null,
        string? executable = null,
        string? nodeConfig = null,
        string? consoleOn = null,
        bool hidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var machine = Path.GetFullPath(root);

        // At the root, because that is the directory this machine's Parley files already live in.
        var log = Path.Combine(machine, Name + ".log");

        // --log rather than a shell redirection, which is the shape this took first. A
        // supervisor's Exec action runs a program, not a command line, so redirecting means
        // wrapping the broker in cmd.exe or sh -c and quoting a path twice -- for the one
        // feature whose absence is silent. The broker writing its own log needs no wrapper on
        // any platform, and the same argument is testable everywhere.
        // **A narrowed entry names a project, not a store and a port.** It wrote
        // `serve --db <path> --listen <url>`, which `serve` no longer accepts at all — so an entry
        // installed with --project would have failed at the next logon, with nothing on screen
        // connecting it to the flags going. The project is the whole of what narrows it now, and
        // where its store is and what port it holds are that project's own business.
        var arguments = project is { Length: > 0 } named
            ? string.Create(CultureInfo.InvariantCulture,
                $"serve --config {Quote(machine)} --project {Quote(named)} --log {Quote(log)}")
            : string.Create(CultureInfo.InvariantCulture,
                $"serve --config {Quote(machine)} --log {Quote(log)}");

        // The catalogue is written in absolute, for the same reason the store is: a logon task
        // has no working directory worth relying on.
        //
        // It is written into the entry at all rather than left to PARLEY_NODE_CONFIG because
        // whether a supervisor sees that variable depends on which supervisor it is -- a logon
        // task inherits the user's environment, a systemd user unit has its own -- and because
        // `setx` does not affect the session it is typed in, so the check somebody runs to
        // confirm it worked is made in the one process that cannot see it. An argument is read
        // the same way by all three, and is visible in `autostart status`.
        if (nodeConfig is { Length: > 0 } catalogue)
        {
            arguments = string.Create(CultureInfo.InvariantCulture,
                $"{arguments} --with-node --node-config {Quote(Path.GetFullPath(catalogue))}");
        }

        // The console, for the same reason as the node and with the same shape. A person's own
        // window is the one attachment nothing can give a turn to, so
        // the console is not a convenience beside the deployment — it is how a person takes part
        // in one without falling into that hole. An entry that started the broker and left the
        // console to be remembered would be an entry that works for agents and not for people.
        //
        // The address is written in whenever there is one, including the default. `autostart
        // status` prints these arguments, and "where is my console" answered by an absence is the
        // question this exists to stop being asked.
        if (consoleOn is { Length: > 0 } front)
        {
            arguments = string.Create(CultureInfo.InvariantCulture,
                $"{arguments} --with-web-ui --web-ui {Quote(front)}");
        }

        // **Last, so that `autostart status` reads it at the end where a person will see it.** The
        // entry starts a console program in somebody's own session, which is what puts a window on
        // their desktop at every logon; this is the broker letting go of it. Written into the
        // arguments rather than into the task's own settings because Task Scheduler's `Hidden` flag
        // controls whether the task is listed, not whether a window appears -- and because a systemd
        // unit and a launchd job read arguments too, while none of them shares that setting.
        if (hidden)
        {
            arguments = string.Create(CultureInfo.InvariantCulture, $"{arguments} --hidden");
        }

        return Compose(executable ?? Environment.ProcessPath ?? "parley", arguments, log);
    }

    /// <summary>
    /// Works out what installing an entry that serves the manifest would do on this machine.
    /// </summary>
    /// <param name="manifestPath">The manifest whose projects the entry serves.</param>
    /// <param name="executable">The broker binary, or null to use the running one.</param>
    /// <param name="consoleOn">
    /// Where the operator's console should listen, or null to install a broker that hosts none.
    /// </param>
    /// <param name="hidden">
    /// Whether the broker should let go of its console window, so that a logon does not put one on
    /// the person's desktop.
    /// </param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException"><paramref name="manifestPath"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// <b>No store and no port, because the manifest holds them.</b> SPEC.md §4.1 gives every
    /// project its own store and endpoint and says one process may host them all; <c>serve</c>
    /// with nothing named does exactly that. An entry written with one explicit store was the
    /// installer's only shape for a long time, and it is what left every project named
    /// afterwards "not answering" until somebody found out the entry had never heard of it.
    /// </para>
    /// <para>
    /// The manifest's path is written in, for the reason the catalogue is in
    /// <see cref="Describe"/>: whether a supervisor sees <c>PARLEY_CONFIG</c> depends on which
    /// supervisor it is, and an argument is read the same way by all three and is visible in
    /// <c>autostart status</c>. The log goes beside the manifest rather than beside a store,
    /// because there is no single store to be beside.
    /// </para>
    /// <para>
    /// <c>--with-node</c> is always written. With several projects the broker hosts a node in each
    /// one whose entry names a catalogue, so a project catalogued after this was installed is
    /// hosted within seconds, without the entry changing — the broker watches the manifest — and
    /// a manifest in which none is catalogued is reported by the broker rather than refused here.
    /// </para>
    /// </remarks>
    public static AutostartPlan DescribeManifest(
        string manifestPath,
        string? executable = null,
        string? consoleOn = null,
        bool hidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        // The root, which used to be "the directory holding the manifest". Same place, said the other
        // way, and taking the parent of it now would put the broker's log beside the Parley folder.
        var root = Path.GetFullPath(manifestPath);

        var log = Path.Combine(root, Name + ".log");

        var arguments = string.Create(CultureInfo.InvariantCulture,
            $"serve --config {Quote(root)} --log {Quote(log)} --with-node");

        if (consoleOn is { Length: > 0 } front)
        {
            arguments = string.Create(CultureInfo.InvariantCulture,
                $"{arguments} --with-web-ui --web-ui {Quote(front)}");
        }

        // **Last, so that `autostart status` reads it at the end where a person will see it.** The
        // entry starts a console program in somebody's own session, which is what puts a window on
        // their desktop at every logon; this is the broker letting go of it. Written into the
        // arguments rather than into the task's own settings because Task Scheduler's `Hidden` flag
        // controls whether the task is listed, not whether a window appears -- and because a systemd
        // unit and a launchd job read arguments too, while none of them shares that setting.
        if (hidden)
        {
            arguments = string.Create(CultureInfo.InvariantCulture, $"{arguments} --hidden");
        }

        return Compose(executable ?? Environment.ProcessPath ?? "parley", arguments, log);
    }

    /// <summary>The plan for this platform, given what it runs and where it logs.</summary>
    /// <param name="binary">The broker binary.</param>
    /// <param name="arguments">What to pass it.</param>
    /// <param name="log">Where its output goes.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// The one place the supervisor is chosen and the unit path derived. The manifest plan used
    /// to be built by describing a placeholder store and overwriting two properties, which
    /// worked and read as a trick — and a trick in the function that decides what runs at
    /// logon is a trick somebody will one day extend.
    /// </remarks>
    private static AutostartPlan Compose(string binary, string arguments, string log) =>
        Detect() switch
        {
            Setup.Supervisor.ScheduledTask => new AutostartPlan(
                Setup.Supervisor.ScheduledTask, Name, binary, arguments, log, null),

            Setup.Supervisor.SystemdUser => new AutostartPlan(
                Setup.Supervisor.SystemdUser, Name, binary, arguments, log,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "systemd", "user", Name + ".service")),

            Setup.Supervisor.LaunchAgent => new AutostartPlan(
                Setup.Supervisor.LaunchAgent, Name, binary, arguments, log,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents", Name + ".plist")),

            _ => new AutostartPlan(
                Setup.Supervisor.Unsupported, Name, binary, arguments, log, null)
        };

    /// <summary>Which supervisor this platform uses.</summary>
    /// <returns>The supervisor.</returns>
    /// <remarks>
    /// Named <c>Detect</c> rather than <c>Supervisor</c> because the record already has a
    /// property by that name. The two would be the same word meaning "which kind is this" and
    /// "which kind would this machine use", which is close enough to confuse and different
    /// enough to matter.
    /// </remarks>
    public static Supervisor Detect() =>
        OperatingSystem.IsWindows() ? Setup.Supervisor.ScheduledTask
        : OperatingSystem.IsMacOS() ? Setup.Supervisor.LaunchAgent
        : OperatingSystem.IsLinux() ? Setup.Supervisor.SystemdUser
        : Setup.Supervisor.Unsupported;

    /// <summary>
    /// The systemd unit that runs this broker.
    /// </summary>
    /// <returns>The unit file's content.</returns>
    /// <remarks>
    /// <para>
    /// A user unit, under <c>~/.config/systemd/user</c>. The system equivalent would start
    /// before anybody logs in and would need root to install, and the broker needs neither.
    /// </para>
    /// <para>
    /// <c>Restart=on-failure</c> rather than <c>always</c>: a broker that exits zero was stopped
    /// deliberately, and restarting it would make stopping it impossible without also disabling
    /// the unit. One that dies is a different matter, and an adapter re-attaches when it comes
    /// back.
    /// </para>
    /// </remarks>
    public string SystemdUnit() =>
        $"""
        [Unit]
        Description=Parley broker: the one process that owns the store
        Documentation=https://github.com/Shrp77/Parley

        [Service]
        Type=simple
        ExecStart={Executable} {Arguments}
        Restart=on-failure
        RestartSec=5
        StandardOutput=append:{LogPath}
        StandardError=append:{LogPath}

        [Install]
        WantedBy=default.target

        """;

    /// <summary>
    /// The launchd property list that runs this broker.
    /// </summary>
    /// <returns>The plist's content.</returns>
    /// <remarks>
    /// <c>RunAtLoad</c> with <c>KeepAlive</c> restricted to a failed exit, matching the systemd
    /// unit: a broker somebody stopped stays stopped.
    /// </remarks>
    public string LaunchAgentPlist() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Escape(Label)}</string>
            <key>ProgramArguments</key>
            <array>
        {ProgramArguments()}
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <dict>
                <key>SuccessfulExit</key>
                <false/>
            </dict>
            <key>StandardOutPath</key>
            <string>{Escape(LogPath)}</string>
            <key>StandardErrorPath</key>
            <string>{Escape(LogPath)}</string>
        </dict>
        </plist>

        """;

    /// <summary>
    /// The executable and its arguments as one command line, for reporting.
    /// </summary>
    /// <returns>The command, quoted only where it needs to be.</returns>
    /// <remarks>
    /// Printed back to whoever ran the install so they can see what will run. Nothing parses it:
    /// every supervisor is handed the program and its arguments separately, which is what stops
    /// this being another place a path with a space in it can come apart.
    /// </remarks>
    public string CommandLine() =>
        $"{Quote(Executable)} {Arguments}";

    /// <summary>
    /// The scheduled task that runs this broker.
    /// </summary>
    /// <param name="user">The account the task runs as, as <c>DOMAIN\\user</c>.</param>
    /// <returns>The task definition, in Task Scheduler's own XML.</returns>
    /// <exception cref="ArgumentException"><paramref name="user"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// <b>Registered through the Task Scheduler API rather than through <c>schtasks.exe</c>,
    /// and that is what makes it installable without elevation.</b> The two are not equivalent:
    /// <c>schtasks /Create</c> refuses with "Access is denied" from an ordinary shell, while the
    /// same task registered through the API succeeds. Both were tried; the difference is not
    /// documented anywhere obvious and cost an afternoon.
    /// </para>
    /// <para>
    /// <b>Three of these settings exist because the defaults would kill a broker on a laptop.</b>
    /// A scheduled task will not start on battery and stops when the machine goes onto battery,
    /// both by default — so a broker installed on a workstation would come up at a desk and
    /// vanish on a train, and every agent would report that it could not be reached. The third
    /// is the execution time limit, which defaults to three days: long enough that nobody
    /// connects the broker dying to the task that started it.
    /// </para>
    /// <para>
    /// <c>LeastPrivilege</c>, explicitly. A broker binds a loopback port and writes under the
    /// user's own profile; a task registered to run with the highest available privileges would
    /// run elevated for ever after, for nothing.
    /// </para>
    /// </remarks>
    public string ScheduledTaskXml(string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Parley broker: the one process that owns the store.</Description>
            <URI>\{Escape(Label)}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(Executable)}</Command>
              <Arguments>{Escape(Arguments)}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    /// <summary>Quotes a value only if it contains something that needs it.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The value, quoted or not.</returns>
    public static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    /// <summary>Escapes a value for XML content.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The escaped value.</returns>
    private static string Escape(string value) => SecurityElement.Escape(value) ?? value;

    /// <summary>Renders the executable and its arguments as plist array entries.</summary>
    /// <returns>The entries, one per line.</returns>
    /// <remarks>
    /// launchd takes an array rather than a command line, so the arguments are split here — and
    /// split on the quoting this class produced rather than naively on spaces, because a store
    /// path with a space in it would otherwise become two arguments and the broker would be
    /// handed a database it could not open.
    /// </remarks>
    private string ProgramArguments()
    {
        var parts = new List<string> { Executable };
        parts.AddRange(Split(Arguments));

        return string.Join(
            Environment.NewLine,
            parts.Select(p => $"        <string>{Escape(p)}</string>"));
    }

    /// <summary>Splits a command line, respecting the quoting <see cref="Quote"/> applies.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>The arguments.</returns>
    /// <remarks>
    /// Public because it is half of a pair: this class quotes a command line and then takes it
    /// apart again for launchd, and a round trip that loses a path with a space in it is the
    /// defect worth checking directly rather than through the plist it ends up in.
    /// </remarks>
    public static IReadOnlyList<string> Split(string arguments)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    _ = current.Clear();
                }
            }
            else
            {
                _ = current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
