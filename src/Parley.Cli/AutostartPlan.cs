using System.Globalization;
using System.Security;

namespace Parley.Cli;

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
    /// One name, so that finding it does not depend on knowing which platform installed it.
    /// Recognisable in a task list without being so generic that it collides with something
    /// else somebody installed.
    /// </remarks>
    public const string Name = "parley-broker";

    /// <summary>
    /// Works out what installing would do on this machine.
    /// </summary>
    /// <param name="databasePath">The store the broker will own.</param>
    /// <param name="listenOn">Where it should listen, or null for the default.</param>
    /// <param name="executable">The broker binary, or null to use the running one.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is blank.</exception>
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
        string databasePath,
        string? listenOn = null,
        string? executable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var store = Path.GetFullPath(databasePath);

        // Beside the store, because that is the directory this deployment already owns and the
        // one somebody looking for the broker will already have open.
        var log = Path.Combine(
            Path.GetDirectoryName(store) ?? ".", Name + ".log");

        // --log rather than a shell redirection, which is the shape this took first. A
        // supervisor's Exec action runs a program, not a command line, so redirecting means
        // wrapping the broker in cmd.exe or sh -c and quoting a path twice -- for the one
        // feature whose absence is silent. The broker writing its own log needs no wrapper on
        // any platform, and the same argument is testable everywhere.
        var arguments = listenOn is { Length: > 0 } endpoint
            ? string.Create(CultureInfo.InvariantCulture,
                $"serve --db {Quote(store)} --listen {Quote(endpoint)} --log {Quote(log)}")
            : string.Create(CultureInfo.InvariantCulture,
                $"serve --db {Quote(store)} --log {Quote(log)}");

        var binary = executable ?? Environment.ProcessPath ?? "parley";

        return Detect() switch
        {
            Cli.Supervisor.ScheduledTask => new AutostartPlan(
                Cli.Supervisor.ScheduledTask, Name, binary, arguments, log, null),

            Cli.Supervisor.SystemdUser => new AutostartPlan(
                Cli.Supervisor.SystemdUser, Name, binary, arguments, log,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "systemd", "user", Name + ".service")),

            Cli.Supervisor.LaunchAgent => new AutostartPlan(
                Cli.Supervisor.LaunchAgent, Name, binary, arguments, log,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents", Name + ".plist")),

            _ => new AutostartPlan(
                Cli.Supervisor.Unsupported, Name, binary, arguments, log, null)
        };
    }

    /// <summary>Which supervisor this platform uses.</summary>
    /// <returns>The supervisor.</returns>
    /// <remarks>
    /// Named <c>Detect</c> rather than <c>Supervisor</c> because the record already has a
    /// property by that name. The two would be the same word meaning "which kind is this" and
    /// "which kind would this machine use", which is close enough to confuse and different
    /// enough to matter.
    /// </remarks>
    public static Supervisor Detect() =>
        OperatingSystem.IsWindows() ? Cli.Supervisor.ScheduledTask
        : OperatingSystem.IsMacOS() ? Cli.Supervisor.LaunchAgent
        : OperatingSystem.IsLinux() ? Cli.Supervisor.SystemdUser
        : Cli.Supervisor.Unsupported;

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
    internal static string Quote(string value) =>
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
