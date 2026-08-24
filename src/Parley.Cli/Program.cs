using Parley.Broker;
using Parley.Cli;
using Parley.Core;
using Parley.Store;

// The operator's way in. SPEC.md §10.2 operations, and nothing else.
//
//   parley --db <path> agents
//   parley --db <path> retire <agent>
//   parley --db <path> reinstate <agent>
//   parley --db <path> export <conversation>
//   parley --db <path> force-conclude <conversation>
//   parley --db <path> prune
//   parley --help
//
// PARLEY_DB is read when --db is absent, so the same variable that configures an agent's server
// configures the operator's view of the same project.
//
// A second binary rather than a flag on parley-mcp. §10.2 warns that an adapter surfacing an
// operator operation into an agent's tool set would breach §10.1, and the safeguards against
// that get progressively more structural: force_conclude is not conclude, OperatorConsole is not
// Broker, and this is not the server. An MCP client launches what it was configured with, and
// what it was configured with has no operator operation compiled into it.

const string Help = """
    parley — the operator operations of SPEC.md 10.2.

      parley [--db <path>] agents                      list every registered agent, whole
      parley [--db <path>] retire <agent>              withdraw a registration
      parley [--db <path>] reinstate <agent>           withdraw a retirement
      parley [--db <path>] export <conversation>       emit every message in a discussion
      parley [--db <path>] force-conclude <conv>       close a discussion over its votes
      parley [--db <path>] prune                       apply the retention policy now
      parley [--db <path>] refusals                    what the message size cap refused

    --db defaults to PARLEY_DB, the same variable the agents' servers are launched with.
    prune reads PARLEY_RETENTION_IDLE_HOURS and PARLEY_RETENTION_MAX_AGE_HOURS from the same
    place, and prints the policy it applied. It removes text permanently; export first if you
    want to keep a discussion.

    These are never available to an agent. parley-mcp, the server an MCP client launches, has
    none of them compiled into it.
    """;

const int Usage = 64;      // EX_USAGE
const int Config = 78;     // EX_CONFIG
const int IoError = 74;    // EX_IOERR

var arguments = new List<string>(args);
string? databasePath = null;

// Asked for rather than fallen into, so it goes to standard output and exits zero. Every other
// path here prints the same text to standard error with a non-zero code, because there it is
// the answer to a mistake. The distinction is what lets `parley --help | less` work while a
// script can still tell a mistyped command from a request for the manual.
if (arguments.Exists(a => a is "--help" or "-h"))
{
    await Console.Out.WriteLineAsync(Help).ConfigureAwait(false);
    return 0;
}

for (var i = 0; i < arguments.Count - 1; i++)
{
    if (arguments[i] is "--db" or "-d")
    {
        databasePath = arguments[i + 1];
        arguments.RemoveRange(i, 2);
        break;
    }
}

// A --db the loop did not consume is one that had no path after it, or a second one. Both used
// to fall through to the stray-option refusal below and be told that '--db' is not an option
// this command takes — which is false, and false in the most confusing direction: it names the
// one option the command does have and denies having it. Found by typing `parley --db` and
// forgetting the path.
var repeated = arguments.FindIndex(a => a is "--db" or "-d");

if (repeated >= 0)
{
    await Console.Error.WriteLineAsync(
        repeated == arguments.Count - 1
            ? $"'{arguments[repeated]}' needs a path after it."
            : $"'{arguments[repeated]}' was given more than once.").ConfigureAwait(false);

    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return Usage;
}

// The name comes from Parley.Core rather than being spelled again here. It used to be a bare
// literal, agreeing with the server's constant by nothing but coincidence — and if they had ever
// diverged, this command would have administered a database the agents were not using while
// reporting success.
databasePath ??= Environment.GetEnvironmentVariable(DeploymentEnvironment.DatabaseVariable);

// Anything left starting with a hyphen is an option this does not have, and refusing it is the
// point rather than tidiness: without this, `parley export --db` reaches the export with "--db"
// as the conversation identifier and runs a real query against it. A flag in the wrong place
// silently becoming data is the worst reading of a mistyped command, because it succeeds.
var stray = arguments.Find(a => a.StartsWith('-'));

if (stray is not null)
{
    await Console.Error.WriteLineAsync($"'{stray}' is not an option this command takes.")
        .ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return Usage;
}

// Before the database, because `parley` on its own is somebody asking what this does, and
// answering "no database" tells them nothing they can act on — least of all that there is a
// list of commands to see. Found by installing it and typing the name.
if (arguments.Count == 0)
{
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);
    return Usage;
}

if (string.IsNullOrWhiteSpace(databasePath))
{
    await Console.Error.WriteLineAsync(
        "No database. Pass --db <path>, or set PARLEY_DB to the same value the agents' servers " +
        "are launched with.").ConfigureAwait(false);

    return Config;
}

// Opened rather than created. An operator command against a project that has never run is a
// mistyped path far more often than it is a first use, and silently creating an empty database
// would answer "no agents registered" to a question about the wrong directory.
if (!File.Exists(databasePath))
{
    await Console.Error.WriteLineAsync(
        $"No Parley database at '{databasePath}'. It is created by the first agent server to " +
        "start in that project, not by this command.").ConfigureAwait(false);

    return Config;
}

SqliteConnectionHolder holder;

try
{
    // Opened, never migrated. This is the operator's tool, and it administers a database the
    // agents' servers created — so migrating from here is never the intent, and doing it is
    // actively dangerous: pointed at somebody's application database by a mistyped path, it
    // wrote Parley's seven tables and triggers into it, bumped user_version from 0 to 1, and
    // reported "no agents registered". A typo should not install a schema.
    holder = new SqliteConnectionHolder(ParleyDatabase.Open(databasePath));

    if (SchemaMigrator.GetUserVersion(holder.Connection) == 0)
    {
        holder.Dispose();

        await Console.Error.WriteLineAsync(
            $"'{databasePath}' is not a Parley database. It has a schema version of zero, which " +
            "means nothing here wrote it. Check the path — this command administers a database " +
            "an agent's server created, and will not create or alter one.").ConfigureAwait(false);

        return Config;
    }
}
#pragma warning disable CA1031 // Do not catch general exception types
catch (Exception ex)
#pragma warning restore CA1031
{
    await Console.Error.WriteLineAsync($"Could not open '{databasePath}': {ex.Message}")
        .ConfigureAwait(false);

    return IoError;
}

using (holder)
{
    var commands = new OperatorCommands(new OperatorConsole(holder.Connection), Console.Out);
    var verb = arguments[0];
    var rest = arguments.Skip(1).ToArray();

    switch (verb)
    {
        case "agents" when rest.Length == 0:
            return await commands.AgentsAsync(CancellationToken.None).ConfigureAwait(false);

        case "retire" when rest.Length == 1:
            return await WithAgent(rest[0], a => commands.RetireAsync(a, CancellationToken.None))
                .ConfigureAwait(false);

        case "reinstate" when rest.Length == 1:
            return await WithAgent(rest[0], a => commands.ReinstateAsync(a, CancellationToken.None))
                .ConfigureAwait(false);

        case "export" when rest.Length == 1:
            return await WithConversation(
                rest[0], id => commands.ExportAsync(id, CancellationToken.None))
                .ConfigureAwait(false);

        case "force-conclude" when rest.Length == 1:
            return await WithConversation(
                rest[0],
                id => commands.ForceConcludeAsync(id, DateTimeOffset.UtcNow, CancellationToken.None))
                .ConfigureAwait(false);

        case "prune" when rest.Length == 0:
            return await PruneAsync(commands).ConfigureAwait(false);

        case "refusals" when rest.Length == 0:
            return await commands.RefusalsAsync(CancellationToken.None).ConfigureAwait(false);

        default:
            await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);
            return Usage;
    }
}

// The policy is read here rather than beside --db, so a variable an operator got wrong stops
// prune and nothing else. Refusing `parley agents` because a retention hour is unparseable
// would be strictly worse: listing changes nothing, and the reason somebody is at a terminal
// asking what is registered is usually that something has already gone wrong.
async Task<int> PruneAsync(OperatorCommands commands)
{
    RetentionPolicy policy;

    try
    {
        policy = RetentionPolicyEnvironment.Read(Environment.GetEnvironmentVariable);
    }
    catch (InvalidOperationException ex)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return Config;
    }

    return await commands.PruneAsync(policy, DateTimeOffset.UtcNow, CancellationToken.None)
        .ConfigureAwait(false);
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
            $"'{name}' is not an agent name. Names are lowercase letters, digits and hyphens, " +
            "starting with a letter, up to 32 characters.").ConfigureAwait(false);

        return Usage;
    }

    return await run(agent).ConfigureAwait(false);
}
