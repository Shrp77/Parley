using System.Globalization;
using System.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Parley.Cli;
using Parley.Configuration;
using Parley.Core;
using Wire = Parley.Transport;

// The operator's way in, and the broker's. SPEC.md §10.2 operations, plus the two processes a
// deployment needs somebody to start.
//
//   parley serve                     the broker of §4: the one process that opens the store
//   parley up                        the node of §4: this machine's supervisor
//   parley registry                  and the rest of §10.2, over the transport
//
// Everything but `serve` is a client. That is the whole shape of the change: this command used
// to open the database itself, which was safe only while it and every agent were on one machine
// — SQLite in WAL mode needs coherent shared memory between every process touching the file, so
// a store on a share is corrupt rather than slow.
//
// A second binary rather than a flag on parley-mcp. §10.2 warns that an adapter surfacing an
// operator operation into an agent's tool set would breach §10.1, and the safeguards against
// that get progressively more structural: force_conclude is not conclude, the Operators service
// is not the Agents service, and this is not the server. An MCP client launches what it was
// configured with, and what it was configured with has no operator operation compiled into it.

const string Help = """
    parley — the broker, this machine's node, and the operator operations of SPEC.md 10.2.

      parley serve [--db <path>] [--listen <url>]      run the broker: the one process that
                   [--log <path>]
                                                       opens the store, and what everything
                                                       else connects to

      parley autostart install [--db <path>]           start the broker when you log in
      parley autostart remove                          stop doing that
      parley autostart status                          say whether it is set up

      parley project list                              what this machine is configured for
      parley project add <name> --db <path>            name a deployment, so commands stop
                        --listen <url>                 being told its paths one at a time
                        [--node-config <path>]
                        [--description <text>]
      parley project remove <name>                     forget how to reach one; deletes nothing

      parley up                                        run this machine's node: report it to
                                                       the broker and start the agents on it
                                                       when a question arrives

      parley enroll scan <dir> [<manifest>]            find repositories and tick the ones to
                     [--all | --none]                  enroll; the flags skip the picker
      parley enroll plan <manifest>                    say what enrolling them would change
      parley enroll apply <manifest>                   configure the ticked ones as agents

      parley registry                                  every registered agent, with its workspace
      parley retire <agent>                            withdraw a registration
      parley reinstate <agent>                         withdraw a retirement
      parley purge <agent>                             DESTROY an agent and its conversations
      parley export <conversation>                     emit every message in a discussion
      parley force-conclude <conv>                     close a discussion over its votes
      parley prune                                     apply the retention policy now
      parley refusals                                  what the message size cap refused
      parley clarifications                            the clarify depth distribution, and whether the cap binds
      parley referrals                                 where questions are sent onward, and to whom
      parley notifications                             how much Notify carries, and who has had enough to judge
      parley waiting <agent>                           how many messages and polls await an agent

      parley --version                                 this build, and the protocol it speaks

    autostart installs a scheduled task on Windows, a systemd user unit on Linux, or a launch
    agent on macOS -- all of them user-level, all installable without administrator rights. They
    start the broker at logon and stop it at logout; a machine that must answer with nobody
    signed in wants a system service instead, and that does need privilege.

    enroll configures several repositories at once: `scan` writes a manifest, you fill in what
    each one owns, `plan` says what would change and `apply` changes it. It never composes an
    owns line and never rewrites a `## Peers` section it did not write. Reading the roster is
    still /parley:write-peers, run once per repository after the last one has registered --
    every agent that registers makes every block already written stale.

    project names deployments so that a machine running more than one stops being told its paths
    a flag at a time. Each project carries a store, an endpoint and optionally a node catalogue;
    --project picks one, and a machine with exactly one configured needs no flag. A machine with
    no projects behaves exactly as it always has -- the manifest is optional and its absence is
    never reported. Where it lives is `parley project list`; --config or PARLEY_CONFIG names a
    different one. Explicit flags and environment variables still win over a project.

    Every command except `serve`, `autostart`, `project` and `enroll` connects to a broker, at
    --broker or PARLEY_BROKER, and
    defaults to the local one. Nothing works until a broker is running: start one with
    `parley serve`, as a service or a scheduled task if you want it there after a reboot.

    serve reads --db, or PARLEY_DB, for the store it owns — and it is the only command that
    touches a file. It migrates the schema, which nothing else does. prune applies the policy
    the broker was started with, from PARLEY_RETENTION_IDLE_HOURS and
    PARLEY_RETENTION_MAX_AGE_HOURS on the broker's machine; it removes text permanently, so
    export first if you want to keep a discussion.

    up runs until you stop it. It tells the broker this machine is here, which is what makes the
    agents registered to it startable rather than stranded, and starts one when a question
    arrives for it. What it can start comes from node.json beside the store, or
    PARLEY_NODE_CONFIG — the broker never holds a command line for a machine it is not on.
    Ctrl+C stops the node and every agent it started.

    These are never available to an agent. parley-mcp, the server an MCP client launches, binds
    the Agents service, which has no method that retires anybody.
    """;

const int Usage = 64;          // EX_USAGE
const int Config = 78;         // EX_CONFIG
const int IoError = 74;        // EX_IOERR
const int Unavailable = 69;    // EX_UNAVAILABLE

var arguments = new List<string>(args);

// Asked for rather than fallen into, so it goes to standard output and exits zero. Every other
// path here prints the same text to standard error with a non-zero code, because there it is
// the answer to a mistake. The distinction is what lets `parley --help | less` work while a
// script can still tell a mistyped command from a request for the manual.
if (arguments.Exists(a => a is "--help" or "-h"))
{
    await Console.Out.WriteLineAsync(Help).ConfigureAwait(false);
    return 0;
}

// Two numbers, because they answer different questions and only one of them is about this
// program. The package version says which build is on PATH; the protocol version says what
// this speaks, and it is the one another implementation cares about — SPEC.md is the contract
// and alternative implementations are expected, so "which Parley is this" cannot be answered
// by a package version that moves for a typo in a help string.
if (arguments.Exists(a => a is "--version" or "-v"))
{
    var build = typeof(OperatorCommands).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    // The informational version carries a source-control suffix when one is built into it.
    // Trimmed, because what somebody typing --version wants is the number they can compare.
    var plus = build.IndexOf('+', StringComparison.Ordinal);

    await Console.Out.WriteLineAsync(string.Create(
        CultureInfo.InvariantCulture,
        $"parley {(plus < 0 ? build : build[..plus])}, speaking Parley protocol " +
        $"{ProtocolVersion.Current}")).ConfigureAwait(false);

    return 0;
}

var selectAllRepositories = TakeFlag("--all");
var selectNoRepositories = TakeFlag("--none");
var databasePath = TakeOption("--db", "-d");
var listenOn = TakeOption("--listen", "-l");
var brokerAddress = TakeOption("--broker", "-b");
var logPath = TakeOption("--log", null);

// Taken here with the rest, and it has to be: everything left starting with a hyphen is refused
// below as an option this does not have, so a flag consumed by the command it belongs to never
// arrives — `--all` did not reach `enroll` until it was taken centrally.
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
// point rather than tidiness: without this, `parley export --broker` reaches the export with
// "--broker" as the conversation identifier and runs a real query against it. A flag in the
// wrong place silently becoming data is the worst reading of a mistyped command, because it
// succeeds.
var stray = arguments.Find(a => a.StartsWith('-'));

if (stray is not null)
{
    await Console.Error.WriteLineAsync($"'{stray}' is not an option this command takes.")
        .ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return Usage;
}

// Before anything else, because `parley` on its own is somebody asking what this does, and
// answering "no broker" tells them nothing they can act on — least of all that there is a list
// of commands to see. Found by installing it and typing the name.
if (arguments.Count == 0)
{
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);
    return Usage;
}

var verb = arguments[0];
var rest = arguments.Skip(1).ToArray();

// The manifest is read once, before any command runs, and a manifest that exists and cannot be
// understood stops everything here. Treating it as "no projects configured" would be the third
// appearance of one failure in this codebase — an unreadable node catalogue read as empty, a
// wrong-typed one cast away — and the consequence is worse at this level: every path afterwards
// silently falls back to a default, so a command acts on a deployment nobody named.
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

// Beside `serve` and the rest because managing configuration must not need a broker: the command
// that fixes "nothing is configured" cannot require something configured.
if (verb == "project")
{
    return await ProjectCommand.RunAsync(
        rest,
        manifestPath,
        new ProjectFields(databasePath, listenOn, nodeConfigPath, projectDescription),
        Console.Out,
        Console.Error).ConfigureAwait(false);
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
    var serving = new List<ProjectSettings>();

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

    // Ctrl+C has to be wired here, and passing no token was a bug rather than a simplification:
    // the broker ran until the process was killed. Starting a WebApplication installs the
    // generic host's ConsoleLifetime, which hooks Console.CancelKeyPress and sets Cancel = true
    // — so the signal was captured and neutralised, the host asked to stop, and the sweep loop
    // waiting on CancellationToken.None never heard about it. Pressing Ctrl+C again could not
    // help either, because the handler stayed registered and kept cancelling the signal.
    //
    // With several projects there are several such handlers, so the interrupt is owned once here,
    // above all of them, and one press must stop every broker started.
    return await UntilInterrupted(token => serving.Count > 0
        ? ServeCommand.RunAsync(serving, logPath, Console.Out, Console.Error, token)
        : ServeCommand.RunAsync(
            databasePath ?? Environment.GetEnvironmentVariable(DeploymentEnvironment.DatabaseVariable),
            listenOn,
            logPath,
            Console.Out,
            Console.Error,
            token)).ConfigureAwait(false);
}

// Selection is refused rather than guessed, for every command that acts on one deployment.
// Several configured and none named, or a name the manifest does not describe, both end here —
// choosing one silently acts on a deployment the operator did not ask for, and falling back
// connects to a different one.
if (!ProjectResolution.TrySelect(projectName, configuredProjects, out var project, out var selectionProblem))
{
    await Console.Error.WriteLineAsync(selectionProblem).ConfigureAwait(false);
    return Config;
}

var resolvedStore = ProjectResolution.Store(databasePath, Environment.GetEnvironmentVariable, project);
var resolvedEndpoint = ProjectResolution.Endpoint(brokerAddress, Environment.GetEnvironmentVariable, project);
var resolvedNodeConfig = ProjectResolution.NodeConfig(nodeConfigPath, Environment.GetEnvironmentVariable, project);

// Beside `serve`, because it installs `serve` and takes the same --db. It is not a client
// either: registering a scheduled task or writing a unit file needs no broker, and needing one
// would make the command that fixes "no broker is running" require a broker.
//
// Its address is resolved here rather than taken from `resolvedEndpoint`, and the difference is
// the whole reason this line exists. `resolvedEndpoint` answers "where do I connect", so it
// consults PARLEY_BROKER — which on a machine whose agents talk to a broker elsewhere holds
// somebody else's host. Feeding that to autostart installs a supervisor that tells a local broker
// to bind a remote address, and the failure surfaces at the next logon, inside a scheduled task,
// where nobody is watching. Where a broker listens comes from --listen or from the project, and
// from nothing else.
if (verb == "autostart" && rest.Length == 1)
{
    var listenAddress = listenOn ?? project?.Endpoint;

    return await AutostartCommand.RunAsync(
        rest[0],
        resolvedStore.Value,
        listenAddress,
        Console.Out,
        Console.Error).ConfigureAwait(false);
}

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

    // The catalogue is passed in resolved, and said out loud before anything is written. This one
    // line is the whole feature: without it, enrolling ten repositories wrote consent into a file
    // in the working directory while the catalogue the node reads sat elsewhere — ten agents
    // recorded as consenting, none startable, and a run that reported success.
    //
    // Printed whether or not a project was selected, which it was not at first. Gating it on a
    // project meant the machine that most needs the line never sees it: the run that produced the
    // defect had no manifest at all, and fell all the way through to `node.json` in whatever
    // directory the command was typed in. A report that appears once somebody has already adopted
    // the fix is not a report.
    //
    // Not in tension with leaving an unconfigured machine alone, either. What must stay invisible
    // is the *absence* of a manifest — a tool that names a missing optional file on every run has
    // made it mandatory — and this names the file it is about to write, which it would name
    // afterwards regardless.
    // `plan` and `apply` only. `scan` writes an enrollment manifest and never opens the catalogue,
    // so naming one there would announce a file the command is not going to touch — which is the
    // same species of wrong as not naming the file it is.
    if (rest is ["plan", ..] or ["apply", ..])
    {
        await Console.Out.WriteLineAsync($"node catalogue: {resolvedNodeConfig}").ConfigureAwait(false);
    }

    return await EnrollCommand.RunAsync(
        rest,
        selectAll,
        resolvedNodeConfig.Value,
        Console.Out,
        Console.Error).ConfigureAwait(false);
}

if (databasePath is not null)
{
    await Console.Error.WriteLineAsync(
        "--db is only for `parley serve` and `parley autostart install`. Every other command reaches the broker over the " +
        "transport and never opens the store: pass --broker, or set " +
        $"{BrokerEndpoint.Variable}.").ConfigureAwait(false);

    return Usage;
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

        case "retire" when rest.Length == 1:
            return await WithAgent(rest[0], a => commands.RetireAsync(a, CancellationToken.None))
                .ConfigureAwait(false);

        case "reinstate" when rest.Length == 1:
            return await WithAgent(rest[0], a => commands.ReinstateAsync(a, CancellationToken.None))
                .ConfigureAwait(false);

        case "purge" when rest.Length == 1:
            return await WithAgent(rest[0], a => commands.PurgeAsync(a, CancellationToken.None))
                .ConfigureAwait(false);

        case "export" when rest.Length == 1:
            return await WithConversation(
                rest[0], id => commands.ExportAsync(id, CancellationToken.None))
                .ConfigureAwait(false);

        case "force-conclude" when rest.Length == 1:
            return await WithConversation(
                rest[0], id => commands.ForceConcludeAsync(id, CancellationToken.None))
                .ConfigureAwait(false);

        case "prune" when rest.Length == 0:
            return await commands.PruneAsync(CancellationToken.None).ConfigureAwait(false);

        case "refusals" when rest.Length == 0:
            return await commands.RefusalsAsync(CancellationToken.None).ConfigureAwait(false);

        case "clarifications" when rest.Length == 0:
            return await commands.ClarificationsAsync(CancellationToken.None).ConfigureAwait(false);

        case "referrals" when rest.Length == 0:
            return await commands.ReferralsAsync(CancellationToken.None).ConfigureAwait(false);

        case "notifications" when rest.Length == 0:
            return await commands.NotificationsAsync(CancellationToken.None).ConfigureAwait(false);

        // Not an operator operation, and it is here because a caller that is not an agent needs
        // it: an editor hook that wants to say "two answers are waiting" before a turn used to
        // read the database, and cannot now. It discloses only counts, for one named agent, and
        // §16 already says the wire cannot tell who is asking — so this adds no disclosure that
        // was not already available to anything that could reach the broker.
        case "waiting" when rest.Length == 1:
            return await WithAgent(
                rest[0],
                async a =>
                {
                    var counts = await new Wire.Agents.AgentsClient(channel).WaitingAsync(
                        new Wire.WaitingRequest { Agent = a.Value }).ConfigureAwait(false);

                    await Console.Out.WriteLineAsync(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{counts.MessagesWaiting} {counts.ClosurePollsWaiting}"))
                        .ConfigureAwait(false);

                    return 0;
                }).ConfigureAwait(false);

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

// The only command that does not return promptly: it runs until the operator stops it.
//
// Ctrl+C is intercepted rather than allowed to kill the process, and that is the whole reason
// this is wired up here instead of inside NodeCommand. The default handling would end this
// process and leave every agent it started orphaned -- resident, holding sessions, answering
// nobody. Cancelling instead gives the node the chance to tell each of them to quit, which is
// one of the three ways SPEC.md §11.1 says a resident agent ends.
async Task<int> UpAsync() =>
    await UntilInterrupted(token =>
        NodeCommand.RunAsync(channel, endpoint, Console.Out, token)).ConfigureAwait(false);

// Runs something that would otherwise never return, and gives the terminal back on Ctrl+C.
//
// Shared by `serve` and `up` because they are the two commands that run until stopped, and
// because having only one of them wired is exactly the state this fixes: `up` documented
// "Ctrl+C stops the node" and honoured it, while `serve` ignored the key entirely.
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

// `parley export ""` used to reach the repository, hit an ArgumentException on the way in, and
// print a .NET stack trace with exit code 127. That is the operator's own input coming back at
// them as a crash — the same shape as the agent-input defects §7.3 exists to prevent, on the
// side of the boundary where nobody was looking. Found by typing it.
//
// Blank only. The schema constrains the character set of a conversation identifier and refuses
// to store one outside it, so an identifier this rejected but storage would have accepted is a
// refusal the operator cannot act on — and an identifier storage would have rejected can only
// ever match nothing, which "no messages in '...'" already says clearly.
async Task<int> WithConversation(string conversationId, Func<string, Task<int>> run)
{
    if (string.IsNullOrWhiteSpace(conversationId))
    {
        await Console.Error.WriteLineAsync(
            "A conversation identifier is required, and blank is not one. Identifiers are chosen " +
            "by the agents that name them on a question or an event; `parley export <id>` is how " +
            "you read one back.").ConfigureAwait(false);

        return Usage;
    }

    return await run(conversationId).ConfigureAwait(false);
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

// An option the loop did not consume is one that had no value after it, or a second one. Both
// used to fall through to the stray-option refusal above and be told that '--db' is not an
// option this command takes — which is false, and false in the most confusing direction: it
// names the one option the command does have and denies having it. Found by typing `parley --db`
// and forgetting the path.
string? Repeated()
{
    var index = arguments.FindIndex(
        a => a is "--db" or "-d" or "--listen" or "-l" or "--broker" or "-b" or "--log");

    return index < 0
        ? null
        : index == arguments.Count - 1
            ? $"'{arguments[index]}' needs a value after it."
            : $"'{arguments[index]}' was given more than once.";
}
