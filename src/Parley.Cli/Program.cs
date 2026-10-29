using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Parley.Setup;
using Grpc.Core;
using Grpc.Net.Client;
using Parley.Cli;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Wire = Parley.Transport;

// The operator's way in, and the broker's. SPEC.md §10.2 operations, plus the two processes a
// deployment needs somebody to start.
//
//   parley serve                     the broker of §4: the one process that opens the store
//   parley up                        the node of §4: this machine's supervisor
//   parley registry                  the reads a script needs, over the transport
//   parley terminal                  everything else: §10.2 is read and changed there
//
// Everything but `serve` is a client: only one process opens the store, because SQLite in WAL
// mode needs coherent shared memory between every process touching the file, so a store on a
// share is corrupt rather than slow.
//
// A second binary rather than a flag on parley-mcp. §10.2 warns that an adapter surfacing an
// operator operation into an agent's tool set would breach §10.1, and the safeguards against
// that get progressively more structural: force_conclude is not conclude, the Operators service
// is not the Agents service, and this is not the server. An MCP client launches what it was
// configured with, and what it was configured with has no operator operation compiled into it.

const string Help = """
    parley — the broker, this machine's node, and what brings a machine up.
    Reading and changing a deployment is `parley` on its own.

      parley serve [--log <path>] [--with-node]        run the broker: the one process that
                   [--with-web-ui [--web-ui <url>]]     opens the stores, and what everything
                   [--heartbeat]                       else connects to. It serves every project
                                                       on this machine, each on the port that
                                                       project was given. --with-node also runs
                                                       this machine's node in it, and
                                                       --with-web-ui starts the web UI beside it.
                                                       --project serves one alone, for testing.
                                                       --heartbeat logs a line a second from
                                                       each broker, and every connection

      parley                                           a deployment from this terminal: the
                                                       projects, who is in each, and their
                                                       conversations as transcripts. Needs a
                                                       terminal, not a browser. Provided by
                                                       the parley-terminal tool, installed
                                                       separately so that changing it does
                                                       not stop the broker. parley terminal
                                                       is the same thing, spelled out

      parley web-ui --open [<url>]                     open the web UI a broker is already serving in your
                                                       browser, and exit. Hosts nothing

      parley web-ui [<url>]                            the operator's web UI in a browser:
                                                       every project, the agents in each, and
                                                       conversations as a transcript that says
                                                       who did not see what. Loopback unless
                                                       you say otherwise. Provided by the
                                                       parley-web-ui tool, installed
                                                       separately so that changing it does
                                                       not stop the broker

      parley autostart install [--web-ui <url>]        start the broker when you log in, with
                               [--no-web-ui]           the web UI. The entry serves every
                               [--node-config <path>]  project on this machine, each with its
                               [--hidden]              own node, including one named later.
                               [--project <name>]      --project serves that one alone, for
                                                       testing. --no-web-ui leaves the web UI
                                                       out, and --hidden stops a console window
                                                       appearing on your desktop at every logon
      parley autostart remove                          stop doing that
      parley autostart status                          say whether it is set up, and what it
                                                       was registered to run

      parley use [<project>] [--shell <name>]          choose the project this terminal's commands act
                 [--clear]                             on: prints the line that sets PARLEY_PROJECT, or
                                                       with no project says which one is in use and why

      parley project list                              what this machine is configured for.
                                                       Projects are created in the terminal or the
                                                       web console, and removed or renamed only in
                                                       the terminal

      parley up [--node-config <path>]                 run this machine's node: report it to
                                                       the broker and start the agents on it
                                                       when a question arrives

      parley enroll <dir> [--all | --none]             find the repositories under a directory,
                    [--describe]                       tick the ones to enroll, and configure and
                                                       start them; --describe changes nothing

      parley registry                                  every registered agent, with its workspace
      parley files list [--conversation <id>]          the files placed: name, size, sha256,
                        [--full]                       who placed each, totals and placements
      parley files place <path> --conversation <id>    place a .md or .txt file from this machine
                        [--name <name>]                in a conversation, as the person
      parley files remove <name>... [--conversation <id>]  remove files whoever placed them;
                        [--yes]                        describes them and asks first
      parley files prune [--yes-remove-every-file]     REMOVE every file; asks for the project's name
      parley files open <name>... [--conversation <id>]  open files to read on this
                                                       machine, or say which machine holds them
      parley files folder [--conversation <id>]        open a conversation's folder, or the whole
                                                       location, or say which machine holds it
      parley files problems [--all]                    what sweeps of the location found: files
                                                       changed on disk and moved to quarantine,
                                                       and entries it could not read, move or delete
      parley files dismiss <id>                        take a problem off the list; deletes
                                                       nothing on disk
      parley open                                      every conversation with an exchange
                                                       still open, and who is in it. What a
                                                       broker restart would interrupt

      parley waiting <agent|role> [--with-files]       how many messages, polls and file notices
                                                       await it; a bare role means that role on
                                                       this machine


      parley --version                                 this build, and the protocol it speaks

    autostart installs a scheduled task on Windows, a systemd user unit on Linux, or a launch
    agent on macOS -- all of them user-level, all installable without administrator rights. They
    start the broker at logon and stop it at logout; a machine that must answer with nobody
    signed in wants a system service instead, and that does need privilege.

    enroll configures several repositories at once, in one act: it finds the git repositories
    under a directory, offers them, and configures, catalogues and starts the ones chosen, so
    that each registers itself. --all and --none skip the picker for scripts and CI; --describe
    reports what would be configured and changes nothing. It needs a project, because the key it
    writes into each repository is `parley-<project>`.

    Enrolling a repository is the consent to start it: a catalogued checkout can be started by a
    peer's request with nobody watching. The command says so, and names the count, before it
    configures anything. It never composes an owns line and never rewrites a `## Peers` section
    it did not write. Reading the roster is still /parley:write-peers, run once per repository
    after the last one has registered -- every agent that registers makes every block already
    written stale.

    project names deployments so that a machine running more than one stops being told its paths
    a flag at a time. Each project carries a store and an endpoint. What this machine can start is
    not one of them: there is one catalogue per machine, and every project starts from it.
    A command acts on the project --project names, or PARLEY_PROJECT (which `parley use <name>`
    prints the line to set, for one terminal), or the one the repository it runs in names -- when
    exactly one of those is a project this machine has -- or the only project there is, and refuses
    rather than guesses. Where several are configured it says on standard error which it acted on.
    A machine that
    names none yet is waiting for its first: the logon entry serves the manifest regardless, and
    starts a project within seconds of it being named. Where the manifest lives is `parley project
    list`; --config or PARLEY_CONFIG names a different one, for a test rig or a second account.
    Explicit flags and environment variables still win over a project.

    Every command except `serve`, `autostart`, `project`, `use`, `terminal` and `web-ui` connects to a broker, at
    --broker or PARLEY_BROKER, and defaults to the local one. Nothing works until a broker is
    running: `parley autostart install` starts one at every logon, serving every project named.

    serve opens its projects' stores — and it is the only command that touches a file. There is no
    way to name one: a project's store is parley.db inside its own folder and its port was claimed
    when it was created, so --project is the only thing that narrows what serve does. It migrates
    the schema, which nothing else does. Pruning, in the terminal and the web UI, applies the policy
    the broker was started with, from PARLEY_RETENTION_IDLE_HOURS and PARLEY_RETENTION_MAX_AGE_HOURS
    on the broker's machine; it removes text permanently, so save a discussion first if you want it.

    up runs until you stop it. It tells the broker this machine is here, which is what makes the
    agents registered to it startable rather than stranded, and starts one when a question
    arrives for it. What it can start comes from --node-config, PARLEY_NODE_CONFIG, or node.json
    beside the manifest, in that order — the broker never holds a command line for a machine it
    is not on. One catalogue for the machine, whatever project is asking: a checkout answering in
    two projects is one startable thing. Ctrl+C stops the node and every agent it started.

    The web UI is installed by default, because that is how a person takes part rather than only
    watches: an agent attached through somebody's own editor window is the one thing nothing can
    hand a turn to, so a web UI remembered separately is the half of a deployment that works for
    agents and not for people. It listens on 127.0.0.1:5280 unless --web-ui says otherwise, and
    if that address is taken the broker says so and keeps serving. --no-web-ui leaves it out.

    A broker and a node are two processes and one deployment, so `serve --with-node` runs both in
    one, and that is what `autostart install` installs: one entry that brings a machine all the
    way up. However many projects it serves, it hosts from the one catalogue this machine has —
    named with `--node-config <path>` on that command, or found beside the manifest. It is not
    read from PARLEY_NODE_CONFIG, because a supervisor starts a process with no shell environment
    and a variable that will not be set at logon is not an answer; `autostart status` prints the
    path the entry will actually use.

    These are never available to an agent. parley-mcp, the server an MCP client launches, binds
    the Agents service, which has no method that retires anybody.
    """;

const int Usage = 64;          // EX_USAGE
const int Config = 78;         // EX_CONFIG
// **The only thing this tool knows about the web UI.** The broker's tool must not learn
// the web stack in order to start one, so what it holds is a command name and an address. The
// name is here rather than inline because `serve --with-web-ui` and the `web-ui` wrapper both
// need it, and two spellings of one command name is a machine where one of them is wrong.
const string WebUiTool = "parley-web-ui";

const int IoError = 74;        // EX_IOERR
const int Unavailable = 69;    // EX_UNAVAILABLE

var arguments = new List<string>(args);

// **A surface's own flags are the surface's to answer.** `parley terminal` and `parley web-ui` forward the command line
// to tools installed apart from this one, so the two questions below are not answered here for them:
// the terminal's version is the one fact somebody asks when the two may have been installed at
// different times.
var forwarding = arguments.Count > 0 && arguments[0] is "terminal" or "web-ui";

// Asked for rather than fallen into, so it goes to standard output and exits zero. Every other
// path here prints the same text to standard error with a non-zero code, because there it is
// the answer to a mistake. The distinction is what lets `parley --help | less` work while a
// script can still tell a mistyped command from a request for the manual.
if (!forwarding && arguments.Exists(a => a is "--help" or "-h"))
{
    await Console.Out.WriteLineAsync(Help).ConfigureAwait(false);
    return 0;
}

// Two numbers, because they answer different questions and only one of them is about this
// program. The package version says which build is on PATH; the protocol version says what
// this speaks, and it is the one another implementation cares about — SPEC.md is the contract
// and alternative implementations are expected, so "which Parley is this" cannot be answered
// by a package version that moves for a typo in a help string.
if (!forwarding && arguments.Exists(a => a is "--version" or "-v"))
{
    // Trimmed of its source-control suffix by BuildIdentity, because what somebody typing this
    // wants is the number they can compare. The broker's startup line keeps the commit, and both
    // read the same property so they cannot disagree about the number.
    await Console.Out.WriteLineAsync(string.Create(
        CultureInfo.InvariantCulture,
        $"parley {BuildIdentity.Version}, speaking Parley protocol " +
        $"{ProtocolVersion.Current}")).ConfigureAwait(false);

    return 0;
}

// `files problems --all` is that command's own, so it is left for `parley files` to read, as its other options are.
var selectAllRepositories = !(arguments.Count > 0 && arguments[0] == "files") && TakeFlag("--all");
var selectNoRepositories = TakeFlag("--none");

// **`enroll --describe` reports what would be configured and changes nothing.** Taken centrally with
// the rest, because everything left starting with a hyphen is refused below as an option this tool
// does not have — a flag consumed by the command it belongs to never arrives.
var describeOnly = TakeFlag("--describe");
var hostNode = TakeFlag("--with-node");
var hostWebUi = TakeFlag("--with-web-ui");
var brokerAddress = TakeOption("--broker", "-b");
var logPath = TakeOption("--log", null);

// **Let go of the console window a logon entry would otherwise put on somebody's desktop.** Read by
// `serve`, which detaches, and by `autostart install`, which writes it into the entry it registers.
var hidden = TakeFlag("--hidden");

// **A line a second from each broker `serve` hosts, and one for every connection**, for finding out why one stopped
// answering. Off unless asked for.
var heartbeat = TakeFlag("--heartbeat");
var webUiOn = TakeOption("--web-ui", null);
var noWebUi = TakeFlag("--no-web-ui");
var withFiles = TakeFlag("--with-files");
var shellName = TakeOption("--shell", null);
var clearProject = TakeFlag("--clear");

// Resolved once. `serve --with-web-ui` and `autostart install` both need the address, and a
// default written in two places is two answers to one question that agree only until somebody
// changes one of them -- here, by installing an entry pointing somewhere the running web UI is not.
var webUiAt = webUiOn;
var webUiAddress = webUiAt ?? WebUiAddress.Loopback;

// Taken here with the rest, and it has to be: everything left starting with a hyphen is refused
// below as an option this does not have, so a flag consumed by the command it belongs to never
// arrives.
var projectName = TakeOption("--project", "-p");
var configPath = TakeOption("--config", null);
var nodeConfigPath = TakeOption("--node-config", null);
var projectDescription = TakeOption("--description", null);

if (Repeated() is { } repeated)
{
    await Console.Error.WriteLineAsync(repeated).ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return Usage;
}

// Anything left starting with a hyphen is an option this does not have, and refusing it is the
// point rather than tidiness: without this, a verb taking an identifier reaches it with
// "--broker" as that identifier and runs a real query against it. A flag in the
// wrong place silently becoming data is the worst reading of a mistyped command, because it
// succeeds.
//
// `parley files` reads its own options, which nothing else has, so exactly those are let through for it. A command line
// forwarded to a surface is not searched at all: that tool has its own parser and refuses what it does not take.
var stray = forwarding
    ? null
    : arguments.Find(a => a.StartsWith('-')
        && !(arguments.Count > 0 && arguments[0] == "files" && FilesCommand.OwnOptions.Contains(a)));

if (stray is not null)
{
    await Console.Error.WriteLineAsync($"'{stray}' is not an option this command takes.")
        .ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return Usage;
}

// **`parley` on its own opens the terminal**: that is where a deployment is read and changed, so
// typing the tool's name should put somebody in it. `parley --help` answers the other question.
//
// **Only at a real terminal.** A redirected stream means a pipe, a script, or CI — `parley | grep`,
// or something probing whether the tool exists — and launching a full-screen application into one
// would hang a pipeline on a surface nobody can see. The same test `ConsolePicker.Available` makes,
// for the same reason; the terminal separately refuses a redirected terminal, so this is the outer
// of two guards rather than the only one.
//
// **And the help is still the answer when the terminal is not there.** `parley-terminal` is a
// separate tool, installed separately so that changing it does not stop the broker — so a machine
// can have this one and not that one. Somebody typing the name on such a machine is asking what
// this does, and an install instruction with no command list beside it answers a question they did
// not ask.
if (arguments.Count == 0)
{
    if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
    {
        // **Named "The terminal" rather than by the command somebody typed**, which would be the
        // bare word this tool is called — and `KeyRuleTests` allows that literal in three files, none
        // of them this one. The rule is right: `"parley"` is a launch key, and a fourth meaning for
        // it should be a decision somebody makes in that list rather than a spelling that drifts in
        // here. The sentence reads better this way in any case.
        // The raw command line, as `parley terminal` forwards it: nothing is left here only because the options were
        // taken above, and `--broker` and `--config` are the terminal's to read (review, lens 3 run 67).
        var opened = await Forwarding.RunAsync(
            "parley-terminal", "Parley.Terminal", "The terminal", args, Console.Error).ConfigureAwait(false);

        if (opened != Unavailable)
        {
            return opened;
        }
    }

    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);
    return Usage;
}

var verb = arguments[0];
var rest = arguments.Skip(1).ToArray();

// **The fourteen operator operations that are the terminal's.** Held as data rather than
// as fourteen case arms so that the refusal, the help and the test that holds them read one list --
// three copies of a set like this agree until one of them is edited.
//
// `registry` and `open` are deliberately not here: the installers invoke both, so every command
// this repository's own scripts and plugin call must go on answering.
string[] Moved =
[
    "retire", "reinstate", "purge", "export", "force-conclude", "prune", "start",
    "archive", "unarchive", "destroy", "refusals", "clarifications", "results", "flags",
];

// Options are taken centrally, above the verb, because a flag consumed by the command it belongs
// to never arrives. The cost is that
// the refusal below, which catches a stray `--broker` before it becomes an identifier,
// cannot see a flag that was legitimately taken for somebody else: every verb
// accepts every option and silently ignores the ones it has no use for.
//
// That is tolerable for a flag whose absence is visible and intolerable for this one. `parley up
// --with-node` is a plausible thing to type -- the flag is about hosting a node, and `up` is the
// command that runs one -- and what it does is exactly what typing nothing does, successfully and
// without comment. Refused where it has no meaning rather than left to be discovered.
// Refused everywhere but `serve`, including on `autostart`, where it would be a flag that reads
// as switching something on and changes nothing: the console is installed by default there, so
// the only meaningful choices are the address and the way out. A flag accepted and ignored is
// worse than one refused, because the person who typed it believes it did something.
if (hostWebUi && verb != "serve")
{
    await Console.Error.WriteLineAsync(
        "--with-web-ui is only for `parley serve`, which is the process that would start the "
        + $"web UI. `parley {verb}` accepts it no more than any other command it does not "
        + "have — and `autostart install` needs no flag to register one, only --web-ui to move "
        + "it or --no-web-ui to leave it out.").ConfigureAwait(false);

    return Usage;
}

if (withFiles && verb != "waiting")
{
    await Console.Error.WriteLineAsync(
        $"--with-files is only for `parley waiting`, where it adds the file gone notices waiting as a third count. "
        + $"`parley {verb}` has no count to add it to.").ConfigureAwait(false);

    return Usage;
}

if ((shellName is not null || clearProject) && verb != "use")
{
    await Console.Error.WriteLineAsync(
        "--shell and --clear are only for `parley use`, which prints the line that sets or clears a terminal's project. "
        + $"`parley {verb}` takes neither.").ConfigureAwait(false);

    return Usage;
}

if ((webUiAt is not null || noWebUi) && verb != "serve" && verb != "autostart")
{
    await Console.Error.WriteLineAsync(
        $"--web-ui and --no-web-ui are for `parley serve` and `parley autostart install`, "
        + $"which are the commands that host a console or install one. `parley {verb}` does "
        + "neither.").ConfigureAwait(false);

    return Usage;
}

if (hostNode && verb != "serve")
{
    await Console.Error.WriteLineAsync(
        $"--with-node is only for `parley serve`, which is the process that would host the node. " +
        $"`parley {verb}` accepts it no more than any other command it does not have.")
        .ConfigureAwait(false);

    return Usage;
}

// The manifest is read once, before any command runs, and a manifest that exists and cannot be
// understood stops everything here. Treating it as "no projects configured" would be the third
// appearance of the failure where an unreadable file reads as empty — and the consequence is worse
// at this level: every path afterwards silently falls back to a default, so a command acts on a
// deployment nobody named.
string manifestPath;

try
{
    manifestPath = ProjectManifest.Locate(configPath, Environment.GetEnvironmentVariable);
}
catch (InvalidOperationException noHome)
{
    // A machine with no per-user configuration directory at all. Reported as a sentence rather
    // than thrown, because it is a fact about the machine that the person running this can act
    // on — and because the alternative to refusing is a manifest path resolved against the
    // working directory, which is the ambiguity everything below exists to remove.
    await Console.Error.WriteLineAsync(noHome.Message).ConfigureAwait(false);
    return Config;
}

if (!ProjectManifest.TryRead(manifestPath, out var configuredProjects, out var manifestProblem))
{
    await Console.Error.WriteLineAsync(manifestProblem).ConfigureAwait(false);
    return Config;
}

// A folder left out costs that project and no other, and is said once on the way in rather than taking every command
// with it.
if (manifestProblem is { Length: > 0 })
{
    await Console.Error.WriteLineAsync(manifestProblem).ConfigureAwait(false);
}

// **The operator operations are not on the command line, and this is where they are answered.**
// Fourteen verbs that read or change a deployment -- retiring and purging agents, exporting,
// archiving and destroying conversations, pruning, and the five health reports -- are
// the terminal's: it is the only surface where the controls can sensibly be offered, and a set of
// them split across three places is three places to keep in step.
//
// **Before any endpoint is resolved and any channel is opened.** A command that has moved must say
// so on a machine with no broker running, which is exactly the machine somebody is most likely to be
// typing it on; refusing after a failed connection would report the wrong problem.
//
// **The sentence never writes `parley` followed by a removed verb.** `PluginTests` extracts every
// `parley <word>` from the whole of this file -- comments and strings included -- and demands set
// equality with the operator skill's list, so a stub naming a removed verb after the word `parley`
// would put it back and fail the build. This comment is written carefully for that reason.
if (Moved.Contains(verb, StringComparer.Ordinal))
{
    await Console.Error.WriteLineAsync(
        $"'{verb}' is not a command here. Reading and changing a deployment happens in the "
        + "terminal, which shows the lists it works on:"
        + $"{Environment.NewLine}{Environment.NewLine}    parley{Environment.NewLine}"
        + $"{Environment.NewLine}A machine with no terminal reaches the web UI over an SSH tunnel; "
        + "the README has the recipe.").ConfigureAwait(false);

    // EX_USAGE, so a script detects it rather than reading an empty success.
    return Usage;
}

// **`use` chooses the project a terminal's commands act on.** A process cannot change the environment of the shell that
// started it, so it prints the line that does; `UseCommand` says why that and not a saved current project.
if (verb == "use")
{
    return await UseCommand.RunAsync(
        rest,
        shellName,
        clearProject,
        projectName,
        configuredProjects,
        Environment.GetEnvironmentVariable(EnrollmentPlan.ProjectVariable),
        RepositoryProjects.Read(Environment.CurrentDirectory),
        Console.Out,
        Console.Error).ConfigureAwait(false);
}

// **Creating, changing and removing a project are the terminal's.**
//
// **Refused here rather than deleted from `ProjectCommand`.** The terminal does not reimplement naming
// a project: `Naming` and `Removing` call this same command in process and print what it said, which
// is what makes a machine configured from a screen configured exactly as one configured from a shell.
// Only the *command line* refuses.
//
// **`list` stays.** Both installers read it — for the manifest path, and to check a project's broker
// actually answers rather than that a task was registered — and every caller in this repository's own
// scripts must go on working. It reads and writes nothing.
string[] MovedProjectActions = ["add", "set", "remove"];

if (verb == "project" && rest.Length > 0 && MovedProjectActions.Contains(rest[0], StringComparer.Ordinal))
{
    await Console.Error.WriteLineAsync(
        $"'project {rest[0]}' is not a command here. A project is created in the terminal or the web "
        + "console, and removed or renamed only in the terminal, where naming one is the whole of it "
        + $"and everything it needs is asked for on the same screen:{Environment.NewLine}{Environment.NewLine}"
        + $"    parley{Environment.NewLine}{Environment.NewLine}"
        + "What this machine is configured for is still `project list`, which reads nothing else "
        + "and changes nothing.").ConfigureAwait(false);

    // EX_USAGE, so a script detects it rather than reading an empty success.
    return Usage;
}

if (verb == "project")
{
    return await ProjectCommand.RunAsync(
        rest,
        manifestPath,
        new ProjectFields(projectDescription),
        Console.Out,
        Console.Error).ConfigureAwait(false);
}

// **`web-ui` forwards.** The web UI is its own tool: `parley` does not reference it, so a machine that
// installs a broker does not install Kestrel and Razor with it. Windows cannot replace a running
// executable either, so hosting it here would mean stopping the broker to change a page, which
// SPEC.md §12 makes expensive.
//
// The raw command line minus the verb is passed on and nothing is parsed here, for the reason
// `terminal` gives: one parser per tool rather than one and an imitation of it.
if (verb == "web-ui")
{
    // Said rather than ignored. Every other command here acts on one deployment, so naming one is
    // the habit; the web UI's first page is the list of them, and silently dropping the name would
    // answer a narrower question than was asked without saying so.
    if (!string.IsNullOrWhiteSpace(projectName))
    {
        await Console.Error.WriteLineAsync(
            $"The web UI is not scoped to one project, so --project {projectName} was not "
            + "used. Every project this machine knows is listed on its first page.")
            .ConfigureAwait(false);
    }

    var forwarded = new List<string>(args);

    forwarded.Remove(verb);

    // **`--open` is answered here, not forwarded: it hosts nothing.** A broker started with
    // `--with-web-ui`, or by an autostart entry that is `--hidden`, already serves the UI and
    // prints its address where nobody is looking. Forwarding would start a second host on a port
    // the first holds. The address is the one given, else the default; an entry installed with
    // `--web-ui <url>` is not consulted, so that case names its address here.
    if (forwarded.Remove("--open"))
    {
        if (forwarded.Count > 1 || forwarded.Exists(a => a.StartsWith('-')))
        {
            await Console.Error.WriteLineAsync(
                "parley web-ui --open takes at most one address, and no other option.")
                .ConfigureAwait(false);

            return Usage;
        }

        var openAt = forwarded.Count == 1 ? forwarded[0] : WebUiAddress.Loopback;

        if (Launch.IsWebAddress(openAt) && !Launch.IsListening(openAt))
        {
            await Console.Error.WriteLineAsync(
                $"Nothing is listening at {openAt}, so the web UI is not running. "
                + "Start it with `parley web-ui`, or restart the broker with --with-web-ui.")
                .ConfigureAwait(false);

            return Unavailable;
        }

        return Launch.Open(said => Console.Error.WriteLine(said), openAt) ? 0 : Unavailable;
    }

    return await Forwarding
        .RunAsync(WebUiTool, "Parley.WebUi", $"parley {verb}", forwarded, Console.Error)
        .ConfigureAwait(false);
}

// Like `web-ui`, `terminal` is not scoped to one project — its first screen is the list of them, and
// it re-reads the manifest while it runs so a project named elsewhere appears without restarting it.
//
// The difference is where it draws. The web UI needs a browser, which a machine may not have; this
// needs a terminal, which is where the operator already is. Either exists because an agent attached
// through somebody's editor window was started by nobody, so nothing can hand it a turn, and a person
// needs somewhere to take part that is not an editor.
// **It forwards, and parses nothing on the way.** The terminal is its own tool, because Windows cannot
// replace a running executable and every terminal change would otherwise stop the broker — which §12
// makes expensive: the node stops and every agent it started is told to quit.
//
// What is passed on is the raw command line minus the verb, not the options this file has already
// taken. `--config` and `--broker` mean the same to both tools and there is one parser for them,
// in the tool that uses them. Anything else is the child's to accept or refuse, including `--project`.
if (verb == "terminal")
{
    var forwarded = new List<string>(args);

    forwarded.Remove("terminal");

    return await Forwarding
        .RunAsync("parley-terminal", "Parley.Terminal", "parley terminal", forwarded, Console.Error)
        .ConfigureAwait(false);
}

// `serve` first and separately, because it is the one command that is not a client. Everything
// below it needs a broker to talk to; this is the broker.
//
// It also selects differently from every other command, which is why it dispatches before the
// selection below. Naming no project is not ambiguity here: SPEC.md §4.1 says one process may
// host many brokers, so a broker told nothing serves everything configured. Every other command
// acts on exactly one deployment and must refuse rather than pick.
if (verb == "serve" && rest.Length == 0)
{
    // **Refused rather than obeyed, because the two together leave nothing anywhere.** Hiding the
    // window takes away the only place the broker's output was going; a log is where it goes
    // instead. Without one, a broker that will not start would say so to a console that has just
    // been closed — invisible, with no record, on the arrangement whose whole purpose is to run
    // unattended. The autostart entry always writes a log, so this only catches a hand-run.
    if (hidden && string.IsNullOrWhiteSpace(logPath))
    {
        await Console.Error.WriteLineAsync(
            "--hidden closes the console this would report to, so it needs somewhere to report "
            + "instead: pass --log <path>. `parley autostart install` always does.")
            .ConfigureAwait(false);

        return Usage;
    }

    var serving = new List<ProjectSettings>();

    // **There is no store to serve but a project's own.** With one source there is no rule about which
    // of two sources wins. Serving is: every project on this machine, or the one named with --project.
    // Nothing else.
    if (!string.IsNullOrWhiteSpace(projectName))
    {
        if (!ProjectResolution.TrySelect(projectName, configuredProjects, out var named, out var whyNot))
        {
            await Console.Error.WriteLineAsync(whyNot).ConfigureAwait(false);
            return Config;
        }

        serving.Add(named!);
    }
    else
    {
        serving.AddRange(configuredProjects);
    }

    // Ctrl+C has to be wired here with a token. Starting a WebApplication installs the generic host's
    // ConsoleLifetime, which hooks Console.CancelKeyPress and sets Cancel = true — so the signal is
    // captured and neutralised, and a sweep loop waiting on CancellationToken.None never hears it.
    //
    // **`--node-config` applies to every project this process serves.** There is one catalogue per
    // machine and the flag names exactly that, so there is nothing to disambiguate.
    var hostedCatalogue = ProjectResolution.NodeConfig(
        nodeConfigPath, Environment.GetEnvironmentVariable, manifestPath);

    var hostedFrom = hostedCatalogue.Value ?? NodeCatalogue.DefaultFileName;

    // There is one catalogue per machine, resolved the same way whether this process serves one
    // project or six, so neither the count nor a watcher restart can change which file a node reads.

    // With several projects there are several such handlers, so the interrupt is owned once here,
    // above all of them, and one press must stop every broker started.
    //
    // The hosted node is built inside this lambda rather than beside it, and that is load-bearing
    // rather than tidy: it runs until stopped exactly as the broker does, so it has to be reached
    // by the same interrupt. Ctrl+C is what tells a node to send every agent it started away
    // (SPEC.md §11.1); a node holding residents outside the interrupt's scope would leave them
    // orphaned, which is the failure `up` was wired against and the reason ShutdownTests reads
    // this file rather than trusting the call to look right.
    //
    // `Hosting` connects to the address the broker actually bound rather than the one it was
    // asked for, so a node is never told to connect to an unbound port.
    // **One table of running agents for this whole process, keyed by identity.** A node is hosted per
    // broker, so two projects asking for one checkout find one process rather than starting two in one
    // working directory. Passed rather than static, because test classes run in parallel.
    //
    // **Declared out here, and swept out here, because this is what owns it.** A node sweeps only a
    // table it made itself, so one project stopping does not send away agents still serving other
    // projects; this table ends when the process does.
    var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

    try
    {
        return await UntilInterrupted(token =>
        {

        // **One catalogue for this machine, whatever project is asking.** What resolves it is the flag,
        // the variable, and then the file beside the manifest.
        var machineCatalogue = ProjectResolution.NodeConfig(
            nodeConfigPath, Environment.GetEnvironmentVariable, manifestPath).Value
            ?? NodeCatalogue.DefaultFileName;

        // **Every project hosts from the same catalogue, and announces what the repositories say it may
        // start for that project.** Nothing belongs to a project, so no project is treated specially.
        async Task<int> HostingNodeFor(
            ProjectSettings project,
            IReadOnlyList<string> addresses,
            TextWriter reporting,
            CancellationToken stopping)
        {
            var from = Path.GetFullPath(machineCatalogue);

            await reporting.WriteLineAsync(
                $"{project.Name}: hosting this machine's node, from {from}.").ConfigureAwait(false);

            using var channel = GrpcChannel.ForAddress(addresses[0]);

            return await NodeCommand.RunAsync(
                channel, addresses[0], from, reporting, project.Name.Value, residents, stopping)
                .ConfigureAwait(false);
        }

        // **Started as a child process, not hosted here.** The web UI is its own tool: `parley` does
        // not reference it, so a machine that installs a broker does not install Kestrel and Razor
        // with it -- which is most machines, since most only run agents.
        //
        // What this knows about the web UI is its command name and its address: the broker's tool
        // must not learn the web stack in order to start one.
        //
        // Contained. The web UI's likeliest failure is
        // not a fault at all but a port already in use -- a second one, or anything else on 5280 --
        // and a broker that refused to serve because a web page could not bind would take a whole
        // deployment down for the one part of it no agent uses.
        async Task<int> HostingWebUi(
            IReadOnlyList<string> addresses, TextWriter reporting, CancellationToken stopping)
        {
            // Set apart, because this is the one address on the screen a person is meant to type
            // into a browser. Ruled rather than coloured: this writer is tee'd into --log, so
            // escape codes would be written into a file somebody greps.
            //
            // ASCII only, for the same reason a supervisor's log is: the section sign in this
            // process's own warnings already comes out mangled on a console whose code page nobody
            // chose, and a banner that renders as question marks is worse than a plain one.
            //
            // Both surfaces, because a person reading it is at a terminal on the machine that runs
            // the broker, and that is frequently not a machine with a browser on it at all.

            Process? child = null;
            string? whyNot = null;

            try
            {
                // The address the broker actually bound, not the one it was asked for.
                //
                // **`CreateNoWindow` is what makes `--hidden` mean anything.** A child console
                // program inherits its parent's console when there is one; once the broker lets go
                // of its console there is nothing to inherit, so Windows gives the child a fresh
                // window. Set unconditionally rather than only when hidden: with a console to
                // inherit this changes nothing, so a condition would be a second thing to keep in
                // step for no gain.
                //
                // ASCII only in this region, comments included: `StartupBannerTests` holds it.
                // What that guard protects is the block printed on a console whose code page
                // nobody chose.
                var start = new ProcessStartInfo(WebUiTool)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                start.ArgumentList.Add("--listen");
                start.ArgumentList.Add(webUiAddress);

                // **The manifest this process serves, not whichever one the web UI would find on its own.** It locates
                // one the way every tool does -- the variable, then this machine's -- so without this
                // `serve --config <file>` would start a web UI showing this machine's projects beside a broker
                // serving somebody else's.
                start.Environment[ProjectManifest.PathVariable] = manifestPath;

                // Only when there is a broker. A machine serving a manifest that names no project yet has none, and
                // the web UI then reads the manifest and says how to name the first one. Never this process's
                // PARLEY_BROKER: the web UI reads it as `--broker`, and here it names somebody else's host, so a
                // console beside a broker with no project would offer that one as this machine.
                start.Environment.Remove(BrokerEndpoint.Variable);

                if (addresses.Count > 0)
                {
                    start.ArgumentList.Add("--broker");
                    start.ArgumentList.Add(addresses[0]);
                }

                child = Process.Start(start);

                if (child is null)
                {
                    throw new InvalidOperationException($"{WebUiTool} could not be started.");
                }
            }
            catch (Exception failure) when (failure is Win32Exception
                                                or InvalidOperationException
                                                or FileNotFoundException)
            {
                // Not installed, most likely, which is an ordinary state now that the surfaces are
                // chosen per machine. Named as a missing tool rather than as a fault, and the
                // broker keeps serving.
                whyNot = failure.Message;
            }

            // **Printed either way, and the failure is a line inside it rather than instead of**
            // **it.** The block names both ways into a deployment, and the reader is at a terminal
            // on the machine running the broker -- frequently not a machine with a browser at all,
            // which is the whole reason `parley terminal` exists. A version that suppressed the block
            // when the web UI could not start would take the terminal line with it, on exactly the
            // run where somebody needs to be told there is another way in.
            var opening = whyNot is null
                ? $"   Web UI:   {webUiAddress}{Environment.NewLine}"
                    + "   Open that one in a browser."
                : $"   Web UI:   did not start ({whyNot}){Environment.NewLine}"
                    + $"   The broker is still serving. Install it: dotnet tool install --global Parley.WebUi";

            var body =
                opening + Environment.NewLine
                + Environment.NewLine
                + $"   Terminal: parley{(configPath is null ? string.Empty : $" terminal --config \"{manifestPath}\"")}{Environment.NewLine}"
                + "   The same deployment, from here, and no browser needed.";

            // Sized to the widest line between the rules. It was the address plus twelve, which
            // was right for as long as the address was the widest thing here, and wrong the moment
            // anything else was -- a rule shorter than what it rules is how a deliberately set-apart
            // block starts looking like a mistake. The failure line is wider than any address, which
            // is the case that would have broken a fixed width.
            var rule = new string(
                '-', body.Split(Environment.NewLine).Max(line => line.Length));

            await reporting.WriteLineAsync(
                $"{Environment.NewLine}  {rule}{Environment.NewLine}"
                + $"{body}{Environment.NewLine}"
                + $"  {rule}{Environment.NewLine}").ConfigureAwait(false);

            try
            {
                // **Not waited on, and not restarted.** A web UI that exits -- because its port was
                // taken, or because somebody killed it -- is a page that is gone, and a broker that
                // ended with it would trade a deployment for a web page. Restarting it would be
                // worse: a port held by something else makes that a loop.
                //
                // What is waited on is the broker's own shutdown, which is what this hook is for.
                return await WaitForShutdown(stopping).ConfigureAwait(false);
            }
            finally
            {
                // **Ended with the broker, so nothing is orphaned.** A web UI outliving the broker
                // it was started for is a page serving a deployment that has stopped, holding the
                // port that the next broker's web UI would want.
                try
                {
                    if (child is { HasExited: false })
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(5000);
                    }
                }
                catch (Exception ending) when (ending is InvalidOperationException
                                                   or Win32Exception
                                                   or NotSupportedException)
                {
                    // Already gone, or not this process's to end. Neither is worth a message on a
                    // path that runs while the broker is shutting down.
                }

                child?.Dispose();
            }
        }

        // The broker stopping is what this hook waits on. A local function rather than a
        // line inline, because both the started and the could-not-start paths end here and a
        // second copy of an infinite wait is a second place to get cancellation wrong.
        static async Task<int> WaitForShutdown(CancellationToken stopping)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The broker stopping is the ordinary way out of here.
            }

            return 0;
        }


        // Passed apart, because they are not the same kind of thing: a node joins one broker, and the
        // web UI is at its best across every project on this machine.
        // The projects, read again whenever any of them changes, so one named while this runs gets its
        // broker within seconds rather than at the next logon. Only when this machine's projects are what
        // is being served: a process told one project by name serves that one, and a machine that named
        // nothing has nothing to watch.
        //
        // **Polled, not watched.** No `FileSystemWatcher` is involved in serving: this wakes on a timer
        // and reads, so a rename in a watched folder cannot raise a creation and a change that each
        // replace the pending look.
        //
        // **One stamp over every project, because there is no single file to stamp.** The manifest file
        // does not exist, so stamping one path would never move and a machine would go on serving
        // whatever it started with. It costs N directory entries and N small reads every two seconds,
        // on a machine with a handful of projects.
        var rereadManifest = ServeCommand.Rereader(manifestPath);

        // **A machine that names no project yet is still a machine being served — but only when
        // something asked it to serve that machine.** That is the entry the installer registers on a
        // machine nobody has configured: it waits, hosting the web UI, and starts the first project
        // named.
        //
        // **`--config` is what distinguishes the two.** A supervisor entry names the root explicitly and
        // should wait, because a project named an hour later must come up without the entry changing. A
        // person typing `parley serve` on a machine with nothing on it has made a mistake, and what they
        // need is a sentence — not a process that sits there for ever writing nothing.
        var awaitingFirstProject = serving.Count == 0
            && string.IsNullOrWhiteSpace(projectName)
            && configPath is not null;

        if (serving.Count == 0 && !awaitingFirstProject)
        {
            Console.Error.WriteLine(
                "No projects on this machine, so there is nothing to serve. They live under "
                + $"{ProjectFolder.ProjectsIn(manifestPath)}; create one in `parley` and "
                + "it gets its own store and its own port. `parley autostart install` then serves "
                + "every project at logon, including any named afterwards.");

            return Task.FromResult(Config);
        }

        var watching = string.IsNullOrWhiteSpace(projectName) && (serving.Count > 0 || awaitingFirstProject);

        // **Last, after every refusal above has had a console to say so on.** Each of those is a
        // person's mistake answered at the moment they made it — no projects, a name this machine
        // does not describe, a hidden run with nowhere to report — and detaching before them would
        // close the one place that answer could appear. From here the broker runs until stopped and
        // everything it has to say goes to the log.
        //
        // The console streams are pointed at nowhere rather than left alone: the standard handles
        // are invalid once the console has gone, so anything still writing through them is writing
        // into a broken handle. Redirecting once here means nothing downstream has to know.
        if (hidden && Windowless.Detach())
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }

        return ServeCommand.RunAsync(
            serving,
            logPath,
            Console.Out,
            Console.Error,
            hostNode ? HostingNodeFor : null,
            hostWebUi ? HostingWebUi : null,
            watching ? rereadManifest : null,
            checkInstall: true,
            heartbeat: heartbeat,
            watchProcess: true,
            cancellationToken: token);
        }).ConfigureAwait(false);
    }
    finally
    {
        // **Every agent this machine started is sent away here, and only here.** A node loop sweeps only a
        // table it created itself; this one it did not create, so ending them belongs to the process that
        // did.
        await NodeCommand.StopEveryoneAsync(residents, Console.Out).ConfigureAwait(false);
    }
}

// Beside `serve`, because it installs `serve`. It is not a client either: registering a
// scheduled task or writing a unit file needs no broker, and needing one would make the command
// that fixes "no broker is running" require a broker.
//
// Above the selection below: every other command acts on one deployment and refuses to choose
// between several; this one installs the process SPEC.md §4.1 says may host them all, so several
// configured and none named is not ambiguity here -- it is the case. Told nothing explicit, it
// installs an entry that serves the manifest; told --project, it installs that one project.
//
// Its address is resolved here rather than through the endpoint resolution below, and the
// difference is the whole reason. That resolution answers "where do I connect", so it consults
// PARLEY_BROKER -- which on a machine whose agents talk to a broker elsewhere holds somebody
// else's host. Feeding that to autostart installs a supervisor that tells a local broker to bind
// a remote address, and the failure surfaces at the next logon, inside a scheduled task, where
// nobody is watching. Where a broker listens comes from the project and from nothing else.
if (verb == "autostart" && rest.Length == 1)
{
    // **A project named is the only way to narrow this.** An entry serves this machine, or the one
    // project it names.
    ProjectSettings? named = null;

    if (!string.IsNullOrWhiteSpace(projectName)
        && !ProjectResolution.TrySelect(projectName, configuredProjects, out named, out var whyNot))
    {
        await Console.Error.WriteLineAsync(whyNot).ConfigureAwait(false);
        return Config;
    }

    var servesManifest = named is null;

    // Refused rather than ignored. An entry serving every project has one catalogue per machine, and
    // a flag naming one has nowhere to go — an install that accepted it and wrote none of it would be
    // the flag-accepted-and-ignored failure this file already refuses elsewhere.
    if (servesManifest && nodeConfigPath is not null)
    {
        await Console.Error.WriteLineAsync(
            "--node-config describes one deployment, and this entry would serve every project under "
            + $"{ProjectFolder.ProjectsIn(manifestPath)}. To install an entry for one project instead, "
            + "name it with --project.").ConfigureAwait(false);

        return Usage;
    }

    // A catalogue nobody named is used only if it is actually there. Every other resolution ends
    // at `node.json`, which is the right answer for a command typed in a directory and the wrong
    // one to write into a logon task: relative there means "wherever the supervisor happens to
    // start", which is not a place anybody put a file. Naming one explicitly is taken at its
    // word, missing file included -- it may be written before the next logon, and refusing would
    // make installing depend on the order two setup steps were done in.
    var entryCatalogue = ProjectResolution.NodeConfig(
        nodeConfigPath, Environment.GetEnvironmentVariable, manifestPath);

    var hostFrom = servesManifest
        ? null
        : entryCatalogue.Source == ValueSource.Default
            ? File.Exists(entryCatalogue.Value) ? entryCatalogue.Value : null
            : entryCatalogue.Value;

    // On by default, which is the whole point of it being here. A person taking part in a
    // deployment does it through the web UI: an agent
    // attached through somebody's window is the one thing nothing can give a turn to, so a
    // web UI that has to be remembered separately is the half of the deployment that works
    // for agents and not for people. `--no-web-ui` is the way out, and it is a flag rather
    // than an absence so that choosing it is visible in the entry `autostart status` prints.
    var webUiFor = noWebUi ? null : webUiAddress;

    return await AutostartCommand.RunAsync(
        rest[0],
        manifestPath,
        named?.Name.Value,
        hostFrom,
        webUiFor,
        servesManifest ? manifestPath : null,
        Console.Out,
        Console.Error,
        CancellationToken.None,
        hidden).ConfigureAwait(false);
}

// **The project a command acts on comes from its context**, in this order: --project; PARLEY_PROJECT, which `parley use`
// prints the line to set for one terminal; the repository the command runs in, when its .mcp.json names exactly one
// project this machine describes; and the only project on the machine. Otherwise it is refused and the projects listed.
// `ProjectResolution.TrySelect` carries why this and not a saved current project.
var projectFromVariable = Environment.GetEnvironmentVariable(EnrollmentPlan.ProjectVariable);
var projectFromRepository = RepositoryProjects.Read(Environment.CurrentDirectory);

if (!ProjectResolution.TrySelect(
        projectName, projectFromVariable, projectFromRepository, configuredProjects,
        out var project, out var projectSource, out var selectionProblem))
{
    // **An address given explicitly is a choice, and is not refused for naming no project.** --broker and PARLEY_BROKER
    // say exactly where to connect, which is all a project would have added for a client, and the help says explicit
    // flags and variables win over a project. Refusing them on a machine naming several would leave every caller that
    // connects by address -- a launch configuration's PARLEY_BROKER read by the waiting hook, a script pointed at one
    // broker -- unable to ask anything. A project named that the manifest does not describe is still refused,
    // address or not.
    var explicitBroker = !string.IsNullOrWhiteSpace(brokerAddress)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BrokerEndpoint.Variable));

    // **`enroll` has no exemption.** One verb writes `parley-<project>` into every repository it
    // configures, so no step can run without being told which deployment is meant: an exemption
    // would mean enrolling a folder into a project nobody named.
    if (!(string.IsNullOrWhiteSpace(projectName) && string.IsNullOrWhiteSpace(projectFromVariable) && explicitBroker))
    {
        await Console.Error.WriteLineAsync(selectionProblem).ConfigureAwait(false);
        return Config;
    }
}

// Said on a machine naming several, because that is where a command could have acted on another deployment: which one,
// and what chose it. To standard error, so a script reading the answer reads only the answer.
if (project is not null && configuredProjects.Count > 1)
{
    await Console.Error.WriteLineAsync($"Project: {project.Name.Value} ({projectSource}).").ConfigureAwait(false);
}

var resolvedEndpoint = ProjectResolution.Endpoint(brokerAddress, Environment.GetEnvironmentVariable, project);
var resolvedNodeConfig = ProjectResolution.NodeConfig(
    nodeConfigPath, Environment.GetEnvironmentVariable, manifestPath);

// Beside `serve` and `autostart` because it is not a client either. It writes launch
// configurations, node entries and the placeholder peers block — files on this machine, against
// values from a manifest — and needs no broker to do any of it. Requiring one would make the
// command that configures a deployment depend on the deployment being configured.
if (verb == "enroll")
{
    if (selectAllRepositories && selectNoRepositories)
    {
        await Console.Error.WriteLineAsync("--all and --none say opposite things; pass one.")
            .ConfigureAwait(false);

        return Usage;
    }

    bool? selectAll = selectAllRepositories ? true : selectNoRepositories ? false : null;

    // The catalogue is passed in resolved, and `enroll` itself says which file it wrote consent
    // into, where the answer is known: a report of the wrong file is worse than no report, because
    // it is believed.
    // The project and the deployment travel with it. `--project` is already resolved above for
    // every command that acts on one deployment, and enrolling is one — a repository becomes an
    // agent *of* something. `ProjectResolution.TrySelect` already refuses to guess when several are
    // configured and none is named, and a second place to answer the question would disagree first
    // on the machine with more than one deployment.
    return await EnrollCommand.RunAsync(
        rest,
        selectAll,
        resolvedNodeConfig,
        Console.Out,
        Console.Error,
        CancellationToken.None,
        project,
        manifestPath,
        describeOnly).ConfigureAwait(false);
}



string endpoint;

try
{
    // Resolution has already applied precedence and may have taken this from a project, so the
    // value is validated here rather than read out of the environment a second time — two reads
    // of one setting eventually disagree, and the one that disagrees silently is this one.
    endpoint = BrokerEndpoint.Read(
        name => name == BrokerEndpoint.Variable ? resolvedEndpoint.Value : null);
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return Config;
}

using var channel = GrpcChannel.ForAddress(endpoint);

try
{
    if (verb == "up" && rest.Length == 0)
    {
        return await UpAsync().ConfigureAwait(false);
    }

    var commands = new OperatorCommands(new Wire.Operators.OperatorsClient(channel), Console.Out);

    switch (verb)
    {
        case "registry" when rest.Length == 0:
            return await commands.RegistryAsync(CancellationToken.None).ConfigureAwait(false);

        case "files":
            return await new FilesCommand(
                    new BrokerClient(new Wire.Operators.OperatorsClient(channel)),
                    Console.Out,
                    Console.Error,
                    Console.In,
                    Parley.Client.Files.LocalLauncher.ForThisMachine,
                    projectName)
                .RunAsync(rest, CancellationToken.None)
                .ConfigureAwait(false);

        case "waiting" when rest.Length == 1:
            return await WithAgentHere(
                rest[0],
                async a =>
                {
                    var counts = await new Wire.Agents.AgentsClient(channel).WaitingAsync(
                        new Wire.WaitingRequest { Agent = a.Value }).ConfigureAwait(false);

                    await Console.Out.WriteLineAsync(withFiles
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"{counts.MessagesWaiting} {counts.ClosurePollsWaiting} {counts.FileNoticesWaiting}")
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"{counts.MessagesWaiting} {counts.ClosurePollsWaiting}"))
                        .ConfigureAwait(false);

                    return 0;
                }).ConfigureAwait(false);

        // **What is in flight, for the one caller that has to know before it stops a broker.**
        // §12 says the node stopping tells every agent it started to quit, and a Request already
        // drained into an agent's inbox and acknowledged is marked delivered -- so killing the agent that holds it
        // leaves the exchange open and owed with nothing running to do the work, flagged as
        // collected and left (§6) against an agent that was killed rather than one that ignored
        // anything.
        //
        // It is the same §10.2 `conversations()` reply filtered to what is open, and it
        // discloses nothing a surface does not; `install.ps1` reads it.
        //
        // One line per conversation, identifier and participants, because the caller is a script
        // and the reader is a person about to make a decision.
        case "open" when rest.Length == 0:
        {
            var page = await new BrokerClient(new Wire.Operators.OperatorsClient(channel))
                .ConversationsAsync(cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            var open = page.Items.Where(c => c.HasOpenExchange).ToList();

            foreach (var conversation in open)
            {
                await Console.Out.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{conversation.Id} {string.Join(",", conversation.Participants.Select(p => p.Value))}"))
                    .ConfigureAwait(false);
            }

            return 0;
        }

        default:
            await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);
            return Usage;
    }
}
catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
{
    // The failure a new deployment meets first, and the one whose message has to earn its place:
    // somebody has typed an operator command and nothing has told them a second process exists.
    await Console.Error.WriteLineAsync(
        $"No broker is listening at {endpoint}. Parley keeps its store in one process and " +
        "everything else connects to it, so nothing works until that process is running: start " +
        $"it with `parley serve`. If it is running elsewhere, pass --broker or set " +
        $"{BrokerEndpoint.Variable}.").ConfigureAwait(false);

    return Unavailable;
}
catch (RpcException failure)
{
    await Console.Error.WriteLineAsync($"The broker refused that: {failure.Status.Detail}")
        .ConfigureAwait(false);

    return IoError;
}

// Runs until the operator stops it, as `serve` does.
//
// Ctrl+C is intercepted rather than allowed to kill the process, and that is the whole reason
// this is wired up here instead of inside NodeCommand. The default handling would end this
// process and leave every agent it started orphaned -- resident, holding sessions, answering
// nobody. Cancelling instead gives the node the chance to tell each of them to quit, which is
// one of the three ways SPEC.md §11.1 says a resident agent ends.
//
// The catalogue is handed over rather than left to the node to find: `--node-config` is taken
// centrally like every other option, and a node that resolved it again would read
// PARLEY_NODE_CONFIG and fall back to the working directory. A supervisor starts a process with no
// shell environment and no working directory worth relying on, which is exactly where the two
// resolutions disagree, and the result reads as a machine that has no agents listed on it.
async Task<int> UpAsync() =>
    await UntilInterrupted(token =>
        NodeCommand.RunAsync(
            channel,
            endpoint,
            resolvedNodeConfig.Value ?? NodeCatalogue.DefaultFileName,
            Console.Out,
            project?.Name.Value,
            null,
            token)).ConfigureAwait(false);

// Runs something that would otherwise never return, and gives the terminal back on Ctrl+C.
//
// Shared by `serve` and `up` because they are the two commands that run until stopped.
//
// A long-running command must own the signal rather than leave it to the default handler. Any
// host started inside it registers its own CancelKeyPress and sets Cancel = true, so the
// default kill never arrives — the signal is swallowed by a component that then waits for
// somebody to observe a token nobody passed.
async Task<int> UntilInterrupted(Func<CancellationToken, Task<int>> run)
{
    using var stopping = new CancellationTokenSource();

    void Stop(object? sender, ConsoleCancelEventArgs e)
    {
        // A second Ctrl+C is left to the default handling on purpose. If shutting down has
        // itself wedged, an operator must still be able to get their terminal back.
        e.Cancel = true;
        Console.CancelKeyPress -= Stop;

        stopping.Cancel();
    }

    Console.CancelKeyPress += Stop;

    try
    {
        return await run(stopping.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
        // Asked for. A broker that reports failure because somebody stopped it teaches operators
        // to ignore its exit code.
        return 0;
    }
    finally
    {
        Console.CancelKeyPress -= Stop;
    }
}

async Task<int> WithAgent(string name, Func<AgentName, Task<int>> run)
{
    if (!AgentName.TryCreate(name, out var agent))
    {
        await Console.Error.WriteLineAsync(
            $"'{name}' is not an agent name. Names are role@machine — a role of lowercase " +
            "letters, digits and hyphens starting with a letter, then the machine it runs on. " +
            "`parley registry` lists them as they are stored.").ConfigureAwait(false);

        return Usage;
    }

    return await run(agent).ConfigureAwait(false);
}

// **`waiting` takes a bare role and completes it with this machine**, as the adapter completes PARLEY_AGENT. A launch
// configuration names a role, so the waiting hook -- which reads one, on the agent's own machine -- has nothing else to
// pass. Only here: a command that acts on an agent still takes the whole name, because acting on one by guessing its
// machine is the wrong way to be helpful.
async Task<int> WithAgentHere(string name, Func<AgentName, Task<int>> run) =>
    !AgentName.TryCreate(name, out _) && MachineName.TryNameHere(name, out var here)
        ? await run(here).ConfigureAwait(false)
        : await WithAgent(name, run).ConfigureAwait(false);

// Consumes an option and its value from the argument list, or returns null.
// Flags have to be taken here, beside the options, rather than read by the command they belong
// to. Everything left starting with a hyphen is refused below on purpose, so a flag consumed
// downstream never reaches its command — it is rejected as a mistyped option first.
bool TakeFlag(string name)
{
    var at = arguments.IndexOf(name);

    if (at < 0)
    {
        return false;
    }

    arguments.RemoveAt(at);

    return true;
}

string? TakeOption(string longName, string? shortName)
{
    for (var i = 0; i < arguments.Count - 1; i++)
    {
        if (arguments[i] == longName || (shortName is not null && arguments[i] == shortName))
        {
            var value = arguments[i + 1];
            arguments.RemoveRange(i, 2);

            return value;
        }
    }

    return null;
}

// An option the loop did not consume is one that had no value after it, or a second one. Naming
// that, rather than letting it fall through to the stray-option refusal, avoids denying an option
// the command does have.
string? Repeated()
{
    var index = arguments.FindIndex(
        a => a is "--broker" or "-b" or "--log");

    return index < 0
        ? null
        : index == arguments.Count - 1
            ? $"'{arguments[index]}' needs a value after it."
            : $"'{arguments[index]}' was given more than once.";
}
