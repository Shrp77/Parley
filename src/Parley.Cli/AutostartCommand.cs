using Parley.Setup;
using System.Diagnostics;
using System.Globalization;

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
    /// <param name="root">Where this machine keeps its Parley files.</param>
    /// <param name="project">The one project to serve, or null to serve every project.</param>
    /// <param name="consoleOn">
    /// Where the installed entry should host the operator's console, or null to install one that
    /// hosts none.
    /// </param>
    /// <param name="nodeConfig">
    /// The node catalogue the installed entry should host this machine's agents from, or null to
    /// install a broker alone.
    /// </param>
    /// <param name="manifestToServe">
    /// The manifest the installed entry should serve every project of, or null to install one
    /// deployment by the explicit paths above. The two are exclusive: an entry serves the
    /// manifest or a store, and the caller has decided which.
    /// </param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <param name="hidden">
    /// Whether an installed entry should tell the broker to let go of its console window. A logon
    /// entry starts a console program in the person's own session, so without this one appears on
    /// their desktop at every logon.
    /// </param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> RunAsync(
        string action,
        string root,
        string? project,
        string? nodeConfig,
        string? consoleOn,
        string? manifestToServe,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default,
        bool hidden = false)
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
                root, project, nodeConfig, consoleOn, manifestToServe, output, error,
                cancellationToken, hidden)
                .ConfigureAwait(false),

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
    /// <param name="root">Where this machine keeps its Parley files.</param>
    /// <param name="project">The one project to serve, or null to serve every project.</param>
    /// <param name="nodeConfig">The node catalogue to host from, or null for a broker alone.</param>
    /// <param name="consoleOn">Where the console should listen, or null to host none.</param>
    /// <param name="manifestToServe">The manifest to serve every project of, or null.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="error">Where to report a failure.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <param name="hidden">Whether the entry should tell the broker to let go of its console window.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> InstallAsync(
        string root,
        string? project,
        string? nodeConfig,
        string? consoleOn,
        string? manifestToServe,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        bool hidden)
    {
        // **The store and the endpoint come from a project or from nowhere.** They were also read from
        // `--db` and `PARLEY_DB`, which is why this once fell back to a variable — and a supervisor
        // starts the broker with no shell environment, so a variable read here named something that
        // would not be set at logon. Both are gone; what is left is the project `--project` selected.
        if (manifestToServe is null && string.IsNullOrWhiteSpace(project))
        {
            await error.WriteLineAsync(
                "This machine has no projects yet, so there is nothing for an entry to serve. Create "
                + "one with `parley project add <name>`, then run this again — the entry will serve "
                + "every project on the machine, including any named afterwards.").ConfigureAwait(false);

            return Config;
        }

        // Absolute once, here, and the same string is both installed and reported. Resolving it
        // twice -- once into the entry and once into the line describing the entry -- is two
        // answers to one question that agree only while nothing changes the working directory
        // between them, and the report is the half nobody can check afterwards.
        var catalogue = nodeConfig is { Length: > 0 } named ? Path.GetFullPath(named) : null;

        // Read before the entry is replaced, because afterwards there is nothing to read. The
        // one thing a switch from an explicit entry to a manifest-serving one can lose is the
        // node: the old entry named a catalogue on its command line, the new one hosts a node
        // only where a project's manifest entry names one, and a manifest written before that
        // mattered names none. Said here, with the command that fixes it, rather than left to
        // arrive days later as a question nobody was started to answer.
        var replacing = manifestToServe is not null ? InstalledEntry.Installed() : null;

        var plan = manifestToServe is not null
            ? AutostartPlan.DescribeManifest(manifestToServe, executable: null, consoleOn, hidden)
            : AutostartPlan.Describe(root, project, executable: null, catalogue, consoleOn, hidden);

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

        // **Said outright rather than only inside the command line above.** It is in `runs:`, as
        // `--web-ui <url>` at the end of a line long enough that nobody reads to the end of it --
        // and it is the one thing this command prints that a person is going to type into a
        // browser. Asked for by the owner on the first install that correctly produced no window:
        // every other fact about the entry was on the screen and the address was not among them.
        //
        // A statement about the entry, not about what is listening. The broker starts the web UI
        // and does not wait on it, so a held port leaves the broker serving and says so in the log
        // -- which is the line above this one. Claiming the page answers would be this command
        // reporting somebody else's success.
        await output.WriteLineAsync(WebUiLine(
            consoleOn, "none, because --no-web-ui was given. Re-run without it to add one."))
            .ConfigureAwait(false);

        // Which half was installed, said plainly, because the two are one entry and therefore
        // indistinguishable in a task list. A deployment whose node was left out looks identical
        // to one whose node is running, until a request to a repository nobody has open waits,
        // never collected, and is flagged.
        if (manifestToServe is not null)
        {
            await output.WriteLineAsync(
                $"  serves: every project in {Path.GetFullPath(manifestToServe)}, and a project " +
                "named later\n         is started within seconds, without reinstalling this.")
                .ConfigureAwait(false);

            // **One catalogue for the machine, named once** (018). This said "in each project whose
            // entry names a catalogue" and the lines under it printed one path per project, because a
            // catalogue used to belong to one — which is exactly what made a checkout startable in one
            // project and not the other. The binding is gone, so a path per project would be this
            // command reading a manifest field nothing else does, and reporting it as fact.
            //
            // Resolved once into a local, for the reason the store path above it is: the line that
            // reports a path and the check that compares it are two answers to one question, and they
            // agree only while nothing moves underneath them.
            var hosting = MachineCatalogue(nodeConfig, manifestToServe);

            await output.WriteLineAsync($"  hosts: this machine's agents, from {hosting}")
                .ConfigureAwait(false);

            foreach (var line in ServedProjects(manifestToServe))
            {
                await output.WriteLineAsync(line).ConfigureAwait(false);
            }

            // A machine just installed names nothing, and the entry is waiting rather than broken: said, with the way on.
            if (Parley.Configuration.ProjectManifest.TryRead(manifestToServe, out var listedProjects, out _)
                && listedProjects.Count == 0)
            {
                await output.WriteLineAsync(
                    "  names: no project yet. Name the first one from `parley terminal`, or with `parley project add`;\n" +
                    "         it starts within seconds, and the web UI shows it.")
                    .ConfigureAwait(false);
            }

            // **Still worth saying, and it now asks a different question.** It used to ask whether any
            // project named a catalogue, because that is where the replaced entry's one would have had
            // to be carried to. Nothing names one any more, so what can be lost is narrower and more
            // specific: the old entry hosted from one file and the new one hosts from another, and
            // every agent catalogued only in the old file stops being startable at the next logon.
            if (replacing is { ServesManifest: false, NodeConfig: { Length: > 0 } dropped }
                && !SamePath(Path.GetFullPath(dropped), hosting))
            {
                await output.WriteLineAsync(
                    $"  WARNING: the entry this replaced started this machine's agents from {dropped}, " +
                    $"and this one\n         hosts from {hosting}. " +
                    "Anything catalogued only in the old file will not\n         be started. Re-run with " +
                    $"`--node-config {AutostartPlan.Quote(dropped)}` to keep the old one, or re-enrol " +
                    "those repositories.")
                    .ConfigureAwait(false);
            }
        }
        else if (catalogue is not null)
        {
            await output.WriteLineAsync($"  hosts: this machine's agents, from {catalogue}")
                .ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(
                "  hosts: nothing. This entry starts the broker alone, so a request to a " +
                "repository\n         nobody has open waits, flagged, until somebody opens it. Install " +
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

    /// <summary>Where the broker this entry starts will look for this machine's catalogue.</summary>
    /// <param name="nodeConfig">A catalogue named on this command line, or null.</param>
    /// <param name="manifestPath">The manifest the entry serves.</param>
    /// <returns>The absolute path.</returns>
    /// <remarks>
    /// <para>
    /// <b>Resolved with no environment, because the entry runs with none.</b> A supervisor starts
    /// the broker at logon, where nothing has set <c>PARLEY_NODE_CONFIG</c> — the paragraph above
    /// the manifest check says so in as many words, about what it serves. Reporting the path
    /// <i>this shell</i> would resolve would therefore be the same defect this output was just
    /// fixed for: a true-looking answer to a question nobody asked, about a run that will not
    /// happen. So the variable is not consulted, and what is printed is what the logon broker gets.
    /// </para>
    /// <para>
    /// The lookup is <see cref="ProjectResolution.NodeConfig"/>'s rather than a second copy of the
    /// rule, for the reason <c>LaunchKey</c> changed assemblies during 018: one rule with two
    /// implementations is a rule that agrees right up until it does not, and this one decides where
    /// consent to start an agent is read from.
    /// </para>
    /// </remarks>
    internal static string MachineCatalogue(string? nodeConfig, string manifestPath) =>
        Path.GetFullPath(
            ProjectResolution.NodeConfig(nodeConfig, _ => null, manifestPath).Value
            ?? Configuration.NodeCatalogue.DefaultFileName);

    /// <summary>Whether two resolved paths are the same file to this platform.</summary>
    /// <param name="left">One path.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they name the same thing.</returns>
    /// <remarks>
    /// Case-folded on Windows and not elsewhere, because the comparison decides whether to print a
    /// warning about agents that will stop being started — and a warning raised by nothing but the
    /// capitalisation of a drive letter is one somebody learns to ignore.
    /// </remarks>
    private static bool SamePath(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Every project this entry serves, with the store and endpoint each one names.</summary>
    /// <param name="manifestPath">The manifest the entry serves.</param>
    /// <returns>The lines to print, or nothing when the manifest cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// <b>Both commands read this manifest already and neither said what was in it.</b> `install`
    /// answered with a rule — <i>each project whose entry names a catalogue</i> — and `status` with a
    /// file path, so a two-project deployment printed exactly what a one-project deployment prints.
    /// The owner of a machine running two projects read both and concluded the second was not being
    /// served; it had been, for hours, with its broker listening and its node able to start three
    /// agents.
    /// </para>
    /// <para>
    /// <b>The paths, because they are rarely beside the manifest and a path somebody has to go and
    /// look up is a path they cannot check against what they meant.</b> The manifest is the one thing
    /// a person is told about, and it is the only one the entry itself names; the store is inside it,
    /// per project, and on the deployment that found this the two projects' stores were in different
    /// directories from each other and from the manifest.
    /// </para>
    /// <para>
    /// <b>This said "three files" and named a catalogue per project until 018.</b> There is one per
    /// machine now, so the caller says it once above these lines rather than repeating one path under
    /// every project — and the reason it had to change is the reason the feature exists: a catalogue
    /// that belonged to a project is what let a checkout be startable in one and stranded in another.
    /// </para>
    /// <para>
    /// Silent when the manifest cannot be read, because the callers have their own words for that and
    /// two messages about one file is how they come to disagree.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> ServedProjects(string manifestPath)
    {
        if (!Configuration.ProjectManifest.TryRead(manifestPath, out var projects, out _))
        {
            return [];
        }

        var lines = new List<string>();

        foreach (var project in projects)
        {
            lines.Add($"    {project.Name.Value}  {project.Endpoint}");
            lines.Add($"      store:     {project.Store}");

            // **The catalogue line was here and is now said once for the machine** (018). What stood
            // here named `node_config` from this project's manifest entry, and the paragraph above
            // still records why three files per project was the right answer then. It is the wrong
            // answer now for the reason the whole feature exists: one catalogue per machine means a
            // path printed per project is either the same path repeated or a field nothing reads.
            // The remaining two really are per project, and really are in different directories.
        }

        return lines;
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
        var plan = AutostartPlan.Describe("placeholder");

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
        var plan = AutostartPlan.Describe("placeholder");

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

        // What it serves, read back from the entry rather than remembered. "Registered" answered
        // a question nobody was asking once a machine could name projects: the one they ask is
        // whether the project they just named will come up at logon, and an entry with one
        // explicit store is the one that will not.
        if (present && InstalledEntry.Installed() is { } entry)
        {
            await output.WriteLineAsync($"  serves: {entry.Serves}").ConfigureAwait(false);

            if (entry.ServesManifest && entry.Manifest is { Length: > 0 } served)
            {
                // **Said here as well, and read out of the entry rather than resolved in this shell**
                // (018). Status lost its catalogue line the moment `ServedProjects` stopped printing
                // one per project — and "which file does the node read" is most of what somebody
                // running this command is asking, since it is the one that decides what can be
                // started. The entry's own `--node-config` is what the logon broker will see.
                await output.WriteLineAsync(
                    $"  hosts: this machine's agents, from {MachineCatalogue(entry.NodeConfig, served)}")
                    .ConfigureAwait(false);

                foreach (var line in ServedProjects(served))
                {
                    await output.WriteLineAsync(line).ConfigureAwait(false);
                }
            }

            // **Read out of the entry, for the same reason `install` says it: the address is in the
            // arguments and nowhere a person can see it.** This is the command somebody runs days
            // later to ask what is installed, which makes it the place they will look for the page's
            // address once they have forgotten it -- and an entry written with `--no-web-ui` answers
            // "why does that address not open" with a sentence instead of with nothing.
            await output.WriteLineAsync(WebUiLine(
                entry.Console,
                "none. This entry starts the broker without one; `parley autostart install` "
                + "registers one by default."))
                .ConfigureAwait(false);

            // **Said either way, because both states are a question somebody asks.** Without it, "why
            // does a black window appear when I log in" is answered by this line or by nothing; with
            // it, somebody wondering where the broker's output went is told it is in the log.
            await output.WriteLineAsync(
                entry.Hidden
                    ? "  window: none. The broker lets go of its console, so everything it says is "
                      + "in the log above."
                    : "  window: a console appears at logon. `parley autostart install --hidden` "
                      + "installs an entry without one.").ConfigureAwait(false);

            if (!entry.ServesManifest)
            {
                // **Said with `--db` until 019 removed it**, so the remedy named a flag the tool
                // refuses. `--project` is what narrows an entry now, and omitting it is what widens
                // one. The same sentence lives in `Naming.cs` and was corrected with it.
                await output.WriteLineAsync(
                    "  A project named with `parley project add` is not served by this entry. " +
                    "Re-run\n  `parley autostart install` with no --project to have it serve every " +
                    "project on this machine.").ConfigureAwait(false);
            }

            // **The one place an operator can be told, and otherwise they are told nothing.** This
            // version removes these spellings; the broker still starts, because every verb ignores
            // options it has no use for, so the entry goes on working and quietly stops starting a
            // web UI. What is left is an absence — a page that no longer answers — with nothing
            // anywhere connecting it to an upgrade. This command's whole job is to say what is
            // installed, so it says this too.
            if (entry.Removed.Count > 0)
            {
                await output.WriteLineAsync(
                    $"  This entry names {string.Join(" and ", entry.Removed)}, which this version "
                    + "no longer accepts.\n  It still starts, and it no longer starts a web UI. Re-run "
                    + "`parley autostart install`\n  to write an entry this version understands.")
                    .ConfigureAwait(false);
            }
        }

        return present ? 0 : 1;
    }

    /// <summary>The line naming where the web UI an entry starts will listen.</summary>
    /// <param name="address">Where it listens, or null or empty when the entry starts none.</param>
    /// <param name="absent">What to say instead when there is none.</param>
    /// <returns>The line, ready to print.</returns>
    /// <remarks>
    /// <para>
    /// **One writer, because `install` and `status` answer the same question and would otherwise
    /// answer it in two spellings.** They differ in what they can say when there is no web UI --
    /// `install` knows `--no-web-ui` was given and `status` only sees an entry that names none --
    /// so that half is the caller's, and the key a person scans for is not.
    /// </para>
    /// <para>
    /// **It deliberately does not contain `--web-ui`.** `install.ps1` reads this command's output
    /// back and scrapes the address out of the `runs:` line by that flag; a second line carrying
    /// the spelling would give the scrape two things to match. `WebUiLineTests` pins it.
    /// </para>
    /// </remarks>
    internal static string WebUiLine(string? address, string absent) =>
        address is { Length: > 0 } front ? $"  web UI: {front}" : $"  web UI: {absent}";

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
        // `CreateNoWindow` for the same reason the web UI child carries it: this runs `schtasks`,
        // `launchctl` or `systemctl`, and a console program started by a parent with no console of
        // its own is given a fresh window. Brief, and a flash nobody asked for is still a flash.
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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
