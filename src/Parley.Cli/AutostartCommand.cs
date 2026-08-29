using System.Diagnostics;
using System.Globalization;
using Parley.Core;

namespace Parley.Cli;

/// <summary>
/// Installs and removes the thing that starts the broker when somebody logs in.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4 says the broker is long-lived — "a Windows Service, systemd unit, or launchd
/// agent" — and nothing implemented it, so every deployment's first lesson was that nothing
/// works until a process somebody has to remember exists is running. That is the worst thing to
/// leave to a README: it is discovered after a reboot, by an agent refusing to launch, in a
/// message about a broker the person had no reason to know about.
/// </para>
/// <para>
/// <b>User-level on every platform, and installable without elevation.</b> That is worth stating
/// because the opposite is widely assumed. A Windows scheduled task registered in the user's own
/// namespace, running as that user, needs no administrator — what needs one is
/// <c>/RL HIGHEST</c>, or running as SYSTEM, and a broker needs neither: it binds a loopback
/// port and writes to a file under the user's own profile.
/// </para>
/// <para>
/// <b>What that costs is honest and worth knowing.</b> A user-level supervisor starts the broker
/// at logon and stops it at logout. For a machine somebody works at, that is exactly right — the
/// agents are editor sessions and there are none when nobody is logged in. For a server that
/// answers questions with nobody signed in, it is wrong, and the answer there is a real Windows
/// Service or a system systemd unit, which does need privilege. <see cref="StatusAsync"/> says
/// which one is installed rather than leaving somebody to infer it.
/// </para>
/// </remarks>
public static class AutostartCommand
{
    private const int Usage = 64;          // EX_USAGE
    private const int Config = 78;         // EX_CONFIG
    private const int Unavailable = 69;    // EX_UNAVAILABLE

    /// <summary>
    /// Runs one autostart subcommand.
    /// </summary>
    /// <param name="action">install, remove or status.</param>
    /// <param name="databasePath">The store, for install.</param>
    /// <param name="listenOn">Where to listen, or null for the default.</param>
    /// <param name="nodeConfig">
    /// The node catalogue the installed entry should host this machine's agents from, or null to
    /// install a broker alone.
    /// </param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> RunAsync(
        string action,
        string? databasePath,
        string? listenOn,
        string? nodeConfig,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (AutostartPlan.Detect() == Supervisor.Unsupported)
        {
            await error.WriteLineAsync(
                "Parley does not know how to start a broker automatically on this platform. " +
                "Run `parley serve` from whatever this system uses to keep a process running.")
                .ConfigureAwait(false);

            return Unavailable;
        }

        return action switch
        {
            "install" => await InstallAsync(
                databasePath, listenOn, nodeConfig, output, error, cancellationToken).ConfigureAwait(false),

            "remove" => await RemoveAsync(output, error, cancellationToken).ConfigureAwait(false),

            "status" => await StatusAsync(output, cancellationToken).ConfigureAwait(false),

            _ => await UnknownAsync(action, error).ConfigureAwait(false)
        };
    }

    private static async Task<int> UnknownAsync(string action, TextWriter error)
    {
        await error.WriteLineAsync(
            $"'{action}' is not something autostart does. It takes install, remove or status.")
            .ConfigureAwait(false);

        return Usage;
    }

    /// <summary>Installs the supervisor entry.</summary>
    /// <param name="databasePath">The store.</param>
    /// <param name="listenOn">Where to listen.</param>
    /// <param name="nodeConfig">The node catalogue to host from, or null for a broker alone.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="error">Where to report a failure.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> InstallAsync(
        string? databasePath,
        string? listenOn,
        string? nodeConfig,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        databasePath ??= Environment.GetEnvironmentVariable(DeploymentEnvironment.DatabaseVariable);

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            await error.WriteLineAsync(
                "No database. Pass --db <path>, or set " +
                $"{DeploymentEnvironment.DatabaseVariable}. A supervisor starts the broker with " +
                "no shell environment, so the store has to be written into the entry rather " +
                "than read from a variable that will not be set at logon.").ConfigureAwait(false);

            return Config;
        }

        // Absolute once, here, and the same string is both installed and reported. Resolving it
        // twice -- once into the entry and once into the line describing the entry -- is two
        // answers to one question that agree only while nothing changes the working directory
        // between them, and the report is the half nobody can check afterwards.
        var catalogue = nodeConfig is { Length: > 0 } named ? Path.GetFullPath(named) : null;

        var plan = AutostartPlan.Describe(databasePath, listenOn, executable: null, catalogue);

        // The directory, not the file. The broker creates the store on first run; what it cannot
        // do is create a directory that a supervisor will try to write a log into before it
        // starts.
        var directory = Path.GetDirectoryName(plan.LogPath);

        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var installed = plan.Supervisor switch
        {
            Supervisor.ScheduledTask when OperatingSystem.IsWindows() =>
                await InstallScheduledTaskAsync(plan, error, cancellationToken).ConfigureAwait(false),

            Supervisor.SystemdUser =>
                await InstallSystemdAsync(plan, error, cancellationToken).ConfigureAwait(false),

            Supervisor.LaunchAgent =>
                await InstallLaunchAgentAsync(plan, error, cancellationToken).ConfigureAwait(false),

            _ => false
        };

        if (!installed)
        {
            return Unavailable;
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Parley's broker is running, and will start when you log in, as {Describe(plan.Supervisor)}."))
            .ConfigureAwait(false);

        await output.WriteLineAsync($"  runs:  {plan.CommandLine()}").ConfigureAwait(false);
        await output.WriteLineAsync($"  logs:  {plan.LogPath}").ConfigureAwait(false);

        // Which half was installed, said plainly, because the two are one entry and therefore
        // indistinguishable in a task list. A deployment whose node was left out looks identical
        // to one whose node is running, until a question to a repository nobody has open waits
        // out its window and is ghosted.
        if (catalogue is not null)
        {
            await output.WriteLineAsync($"  hosts: this machine's agents, from {catalogue}")
                .ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(
                "  hosts: nothing. This entry starts the broker alone, so a question to a " +
                "repository\n         nobody has open will wait and then be ghosted. Install " +
                "with --node-config <path>\n         to have it start this machine's agents too.")
                .ConfigureAwait(false);
        }

        // Said every time rather than buried in documentation, because it is the one property of
        // this arrangement somebody can be surprised by -- and the surprise arrives days later,
        // on a machine nobody is signed into, as agents that will not answer.
        await output.WriteLineAsync(
            "  It starts at logon and stops at logout. A machine that must answer with nobody " +
            "signed in needs a system service instead, which does need administrator rights.")
            .ConfigureAwait(false);

        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<bool> InstallScheduledTaskAsync(
        AutostartPlan plan, TextWriter error, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        try
        {
            ScheduledTasks.Register(plan.Label, plan.ScheduledTaskXml(CurrentUser()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"Could not register the scheduled task: {exception.Message}").ConfigureAwait(false);

            return false;
        }

        // Started here rather than left to the trigger, so that installing means the same thing
        // on all three supervisors. `systemctl --user enable --now` starts the unit and a launch
        // agent with RunAtLoad starts when it is bootstrapped; a logon trigger does not fire
        // until the next logon, which made this the one platform where a successful install
        // still left nothing listening.
        try
        {
            ScheduledTasks.Start(plan.Label);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Registered, and that is the part that had to succeed -- the task will start at the
            // next logon whatever happened here. Reported rather than fatal, because failing the
            // install would throw away a correct registration over a broker that can be started
            // by hand.
            await error.WriteLineAsync(
                $"Registered the task, but could not start it now: {exception.Message}. It will " +
                "start at your next logon; until then run `parley serve` yourself.")
                .ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>The account a scheduled task should run as.</summary>
    /// <returns>The user, qualified by domain where there is one.</returns>
    /// <remarks>
    /// Qualified, because a logon trigger names the account it triggers for and an unqualified
    /// name is ambiguous on a machine joined to a domain.
    /// </remarks>
    private static string CurrentUser() =>
        string.IsNullOrEmpty(Environment.UserDomainName)
            ? Environment.UserName
            : Environment.UserDomainName + "\\" + Environment.UserName;

    private static async Task<bool> InstallSystemdAsync(
        AutostartPlan plan, TextWriter error, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(plan.UnitPath!)!);

        await File.WriteAllTextAsync(plan.UnitPath!, plan.SystemdUnit(), cancellationToken)
            .ConfigureAwait(false);

        _ = await RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken)
            .ConfigureAwait(false);

        var enabled = await RunAsync(
            "systemctl", ["--user", "enable", "--now", plan.Label + ".service"], cancellationToken)
            .ConfigureAwait(false);

        if (enabled.ExitCode == 0)
        {
            return true;
        }

        // The unit file is left where it is. It is the useful artefact even when enabling
        // failed -- somebody without a user bus can still start it by hand, and deleting it
        // would leave them nothing to look at.
        await error.WriteLineAsync(
            $"Wrote {plan.UnitPath}, but could not enable it: {enabled.Output.Trim()}")
            .ConfigureAwait(false);

        return false;
    }

    private static async Task<bool> InstallLaunchAgentAsync(
        AutostartPlan plan, TextWriter error, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(plan.UnitPath!)!);

        await File.WriteAllTextAsync(plan.UnitPath!, plan.LaunchAgentPlist(), cancellationToken)
            .ConfigureAwait(false);

        // Unloaded first, so that reinstalling over a running agent replaces it rather than
        // being refused for already existing. The failure is ignored: not being loaded is the
        // ordinary case on a first install.
        var uid = await UidAsync(cancellationToken).ConfigureAwait(false);

        _ = await RunAsync(
            "launchctl", ["bootout", $"gui/{uid}/{plan.Label}"], cancellationToken)
            .ConfigureAwait(false);

        var loaded = await RunAsync(
            "launchctl", ["bootstrap", $"gui/{uid}", plan.UnitPath!], cancellationToken)
            .ConfigureAwait(false);

        if (loaded.ExitCode == 0)
        {
            return true;
        }

        await error.WriteLineAsync(
            $"Wrote {plan.UnitPath}, but could not load it: {loaded.Output.Trim()}")
            .ConfigureAwait(false);

        return false;
    }

    /// <summary>Removes the supervisor entry.</summary>
    /// <param name="output">Where to report.</param>
    /// <param name="error">Where to report a failure.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// Removing the entry does not stop a broker that is already running. That is deliberate:
    /// stopping one would end every attached agent's session mid-exchange, and somebody removing
    /// an autostart entry is usually rearranging their machine rather than asking for that.
    /// </remarks>
    private static async Task<int> RemoveAsync(
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var plan = AutostartPlan.Describe("placeholder.db");

        var removed = plan.Supervisor switch
        {
            Supervisor.ScheduledTask when OperatingSystem.IsWindows() =>
                ScheduledTasks.Delete(plan.Label),

            Supervisor.SystemdUser => await RemoveSystemdAsync(plan, cancellationToken)
                .ConfigureAwait(false),

            Supervisor.LaunchAgent => await RemoveLaunchAgentAsync(plan, cancellationToken)
                .ConfigureAwait(false),

            _ => false
        };

        if (!removed)
        {
            await error.WriteLineAsync(
                "Nothing to remove: Parley's broker is not registered to start at logon here.")
                .ConfigureAwait(false);

            return 1;
        }

        await output.WriteLineAsync(
            "Removed. A broker that is already running keeps running; stop it yourself when the " +
            "agents attached to it are done.").ConfigureAwait(false);

        return 0;
    }

    private static async Task<bool> RemoveSystemdAsync(
        AutostartPlan plan, CancellationToken cancellationToken)
    {
        var disabled = await RunAsync(
            "systemctl", ["--user", "disable", plan.Label + ".service"], cancellationToken)
            .ConfigureAwait(false);

        if (File.Exists(plan.UnitPath))
        {
            File.Delete(plan.UnitPath!);
        }

        _ = await RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken)
            .ConfigureAwait(false);

        return disabled.ExitCode == 0;
    }

    private static async Task<bool> RemoveLaunchAgentAsync(
        AutostartPlan plan, CancellationToken cancellationToken)
    {
        var uid = await UidAsync(cancellationToken).ConfigureAwait(false);

        var removed = await RunAsync(
            "launchctl", ["bootout", $"gui/{uid}/{plan.Label}"], cancellationToken)
            .ConfigureAwait(false);

        var existed = File.Exists(plan.UnitPath);

        if (existed)
        {
            File.Delete(plan.UnitPath!);
        }

        return removed.ExitCode == 0 || existed;
    }

    /// <summary>Reports whether the broker is registered to start at logon.</summary>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Zero if it is, one if it is not.</returns>
    private static async Task<int> StatusAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var plan = AutostartPlan.Describe("placeholder.db");

        var present = plan.Supervisor switch
        {
            Supervisor.ScheduledTask when OperatingSystem.IsWindows() =>
                ScheduledTasks.Exists(plan.Label),

            _ => File.Exists(plan.UnitPath)
        };

        await output.WriteLineAsync(
            present
                ? $"Parley's broker starts at logon, as {Describe(plan.Supervisor)}."
                : "Parley's broker does not start on its own here. `parley autostart install` " +
                  "registers it; until then something has to run `parley serve`.")
            .ConfigureAwait(false);

        return present ? 0 : 1;
    }

    /// <summary>Names a supervisor the way somebody would look for it.</summary>
    /// <param name="supervisor">The supervisor.</param>
    /// <returns>Its name, and where to find it.</returns>
    private static string Describe(Supervisor supervisor) => supervisor switch
    {
        Supervisor.ScheduledTask => $"a scheduled task called '{AutostartPlan.Name}'",
        Supervisor.SystemdUser => $"the systemd user unit '{AutostartPlan.Name}.service'",
        Supervisor.LaunchAgent => $"the launch agent '{AutostartPlan.Name}'",
        _ => "nothing this build knows about"
    };

    /// <summary>The current user's numeric id, for launchd's domain syntax.</summary>
    /// <returns>The identifier.</returns>
    /// <remarks>
    /// launchd addresses a user's domain as <c>gui/&lt;uid&gt;</c> and there is no managed way to
    /// ask for it. Asked of <c>id</c> rather than of libc through an interop declaration, which
    /// would oblige the whole project to allow unsafe code for one integer -- a poor trade, and
    /// this file already shells out to three supervisors.
    /// </remarks>
    private static async Task<string> UidAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("id", ["-u"], cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 ? result.Output.Trim() : "0";
    }

    /// <summary>Runs a supervisor's own command-line tool and collects what it said.</summary>
    /// <param name="fileName">The tool.</param>
    /// <param name="arguments">Its arguments, passed separately so nothing re-quotes them.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Its exit code and its combined output.</returns>
    /// <remarks>
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/> rather than a single
    /// string, which is the whole reason this helper exists. A command line assembled by hand
    /// and re-parsed by the operating system is where the quoting defects live, and one of them
    /// is what made the hand-written version of this command fail with "Invalid argument/option"
    /// naming the entire line back.
    /// </remarks>
    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return (1, $"could not start {fileName}");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode,
                    (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A supervisor's tool that is not installed, or not on PATH. Reported as its own
            // failure rather than thrown, because "systemctl is not here" is a fact about the
            // machine and the caller prints it.
            return (1, exception.Message);
        }
    }
}
