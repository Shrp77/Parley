using System.Globalization;
using System.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Parley.Cli;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
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
                   [--log <path>] [--with-node]
                                                       opens the store, and what everything
                                                       else connects to. --with-node also runs
                                                       this machine's node in it

      parley console [<url>]                           the operator's console in a browser:
                                                       every project, the agents in each, and
                                                       conversations as a transcript that says
                                                       who did not see what. Loopback unless
                                                       you say otherwise

      parley autostart install [--db <path>]           start the broker when you log in, and
                               [--node-config <path>]  this machine's node with it
      parley autostart remove                          stop doing that
      parley autostart status                          say whether it is set up

      parley project list                              what this machine is configured for
      parley project add <name> --db <path>            name a deployment, so commands stop
                        --listen <url>                 being told its paths one at a time
                        [--node-config <path>]
                        [--description <text>]
      parley project remove <name>                     forget how to reach one; deletes nothing

      parley up [--node-config <path>]                 run this machine's node: report it to
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
    arrives for it. What it can start comes from --node-config, PARLEY_NODE_CONFIG, the selected
    project, or node.json in the working directory, in that order — the broker never holds a
    command line for a machine it is not on. Ctrl+C stops the node and every agent it started.

    A broker and a node are two processes and one deployment, so `serve --with-node` runs both in
    one, and that is what `autostart install` installs: one entry that brings a machine all the
    way up. Give it --node-config, because a supervisor starts a process with no shell
    environment and no working directory worth relying on, and the two fallbacks above are both
    one of those. Without it the entry starts a broker alone and says so.

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
var hostNode = TakeFlag("--with-node");
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

// Options are taken centrally, above the verb, because a flag consumed by the command it belongs
// to never arrives -- `--all` did not reach `enroll` until it was taken here. The cost is that
// the refusal below, which catches `parley export --broker` before "--broker" becomes a
// conversation id, cannot see a flag that was legitimately taken for somebody else: every verb
// accepts every option and silently ignores the ones it has no use for.
//
// That is tolerable for a flag whose absence is visible and intolerable for this one. `parley up
// --with-node` is a plausible thing to type -- the flag is about hosting a node, and `up` is the
// command that runs one -- and what it does is exactly what typing nothing does, successfully and
// without comment. Refused where it has no meaning rather than left to be discovered.
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

// Beside `project` and above the selection below, because the console is not scoped to one
// project — it is the page you choose one from, and it reads the manifest itself. Naming a
// project here would be naming one for a view whose first screen is the list of them.
//
// It is a client like every other command and opens no database. That it is served rather than
// printed changes nothing about which side of §4 it sits on: it holds a connection to each
// project's broker and asks, exactly as `parley registry` does.
if (verb == "console")
{
    var listenTo = rest.Length > 0 ? rest[0] : listenOn;

    // Said rather than ignored. Every other command here acts on one deployment, so naming one
    // is the habit; the console's first page is the list of them, and silently dropping the name
    // would answer a narrower question than was asked without saying so.
    if (!string.IsNullOrWhiteSpace(projectName))
    {
        await Console.Error.WriteLineAsync(
            $"The console is not scoped to one project, so --project {projectName} was not "
            + "used. Every project this machine knows is listed on its first page.")
            .ConfigureAwait(false);
    }

    // Checked here rather than left to Kestrel, which refuses a malformed address several frames
    // deep in a stack trace that says nothing about what was typed.
    if (listenTo is not null
        && !Uri.TryCreate(listenTo, UriKind.Absolute, out _))
    {
        await Console.Error.WriteLineAsync(
            $"'{listenTo}' is not an address the console can bind. Give it one like "
            + $"{Parley.Web.ConsoleHost.LoopbackAddress}.").ConfigureAwait(false);

        return Usage;
    }

    // The broker every other command here would have connected to, offered when this machine has
    // named no projects. Running by explicit paths is supported (§4.1), and on such a machine a
    // console that listed nothing would be the only client unable to reach a broker the rest of
    // the tool talks to happily — while saying "no projects", which is true and reads as
    // "nothing to see".
    var fallbackBroker = ProjectResolution
        .Endpoint(brokerAddress, Environment.GetEnvironmentVariable, null).Value;

    await Console.Out.WriteLineAsync(
        $"Parley console on {listenTo ?? Parley.Web.ConsoleHost.LoopbackAddress}. Ctrl+C stops it.")
        .ConfigureAwait(false);

    await Parley.Web.ConsoleHost
        .RunAsync([], listenTo, Console.Error, fallbackBroker)
        .ConfigureAwait(false);

    return 0;
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
    // A node belongs to one broker, so hosting one here needs the ambiguity resolved rather than
    // guessed -- the same rule every other command follows, arriving at the one command that is
    // allowed to serve everything at once. Refused with the fix in it, because the alternative is
    // a machine silently joined to whichever deployment happened to be listed first.
    if (hostNode && serving.Count > 1)
    {
        var serves = string.Create(
            CultureInfo.InvariantCulture,
            $"--with-node needs one project, and this process would serve {serving.Count}.");

        await Console.Error.WriteLineAsync(
            serves + " A node joins one broker because a machine has one identity " +
            "(SPEC.md §4.1.1). Name the deployment this machine belongs to with --project, or " +
            "run `parley up` beside the broker instead.").ConfigureAwait(false);

        return Config;
    }

    var hostedCatalogue = ProjectResolution.NodeConfig(
        nodeConfigPath,
        Environment.GetEnvironmentVariable,
        serving.Count == 1 ? serving[0] : null);

    var hostedFrom = hostedCatalogue.Value ?? NodeCatalogue.DefaultFileName;
    var fromEnvironment = Environment.GetEnvironmentVariable(DeploymentEnvironment.DatabaseVariable);

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
    // asked for, because --listen may name port 0 and a node told to connect to port 0 connects
    // to nothing.
    return await UntilInterrupted(token =>
    {
        async Task<int> Hosting(
            IReadOnlyList<string> addresses, TextWriter reporting, CancellationToken stopping)
        {
            await reporting.WriteLineAsync($"Hosting this machine's node, from {hostedCatalogue}.")
                .ConfigureAwait(false);

            using var channel = GrpcChannel.ForAddress(addresses[0]);

            return await NodeCommand.RunAsync(
                channel, addresses[0], hostedFrom, reporting, stopping).ConfigureAwait(false);
        }

        return serving.Count > 0
            ? ServeCommand.RunAsync(
                serving, logPath, Console.Out, Console.Error, hostNode ? Hosting : null, token)
            : ServeCommand.RunAsync(
                databasePath ?? fromEnvironment,
                listenOn,
                logPath,
                Console.Out,
                Console.Error,
                hostNode ? Hosting : null,
                token);
    }).ConfigureAwait(false);
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

    // A catalogue nobody named is used only if it is actually there. Every other resolution ends
    // at `node.json`, which is the right answer for a command typed in a directory and the wrong
    // one to write into a logon task: relative there means "wherever the supervisor happens to
    // start", which is not a place anybody put a file. Naming one explicitly is taken at its
    // word, missing file included -- it may be written before the next logon, and refusing would
    // make installing depend on the order two setup steps were done in.
    var hostFrom = resolvedNodeConfig.Source == ValueSource.Default
        ? File.Exists(resolvedNodeConfig.Value) ? resolvedNodeConfig.Value : null
        : resolvedNodeConfig.Value;

    return await AutostartCommand.RunAsync(
        rest[0],
        resolvedStore.Value,
        listenAddress,
        hostFrom,
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
    // The catalogue is named by `enroll` itself rather than here, and this comment is what is
    // left of the line that used to do it. Printing the entry point's resolution meant printing a
    // path before the manifest's own `node_config` had been read, so a run could announce
    // `node.json` and write somewhere else entirely — a report of the wrong file being worse than
    // no report, because it is believed. Everything the original line argued for still holds; it
    // simply belongs where the answer is known.
    return await EnrollCommand.RunAsync(
        rest,
        selectAll,
        resolvedNodeConfig,
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
//
// The catalogue is handed over rather than left to the node to find, and that was a defect
// rather than a refactor: `--node-config` is taken centrally like every other option, was
// honoured by `enroll`, and was silently ignored here — the node read PARLEY_NODE_CONFIG again
// and fell back to the working directory. A supervisor starts a process with no shell
// environment and no working directory worth relying on, which is exactly where the two
// resolutions disagree, and the result reads as a machine that has no agents listed on it.
async Task<int> UpAsync() =>
    await UntilInterrupted(token =>
        NodeCommand.RunAsync(
            channel,
            endpoint,
            resolvedNodeConfig.Value ?? NodeCatalogue.DefaultFileName,
            Console.Out,
            token)).ConfigureAwait(false);

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
