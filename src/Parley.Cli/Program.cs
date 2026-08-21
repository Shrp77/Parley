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

    --db defaults to PARLEY_DB, the same variable the agents' servers are launched with.

    These are never available to an agent. parley-mcp, the server an MCP client launches, has
    none of them compiled into it.
    """;

const int Usage = 64;      // EX_USAGE
const int Config = 78;     // EX_CONFIG
const int IoError = 74;    // EX_IOERR

var arguments = new List<string>(args);
string? databasePath = null;

for (var i = 0; i < arguments.Count - 1; i++)
{
    if (arguments[i] is "--db" or "-d")
    {
        databasePath = arguments[i + 1];
        arguments.RemoveRange(i, 2);
        break;
    }
}

databasePath ??= Environment.GetEnvironmentVariable("PARLEY_DB");

if (string.IsNullOrWhiteSpace(databasePath))
{
    await Console.Error.WriteLineAsync(
        "No database. Pass --db <path>, or set PARLEY_DB to the same value the agents' servers " +
        "are launched with.").ConfigureAwait(false);

    return Config;
}

if (arguments.Count == 0)
{
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);
    return Usage;
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
    holder = new SqliteConnectionHolder(ParleyDatabase.OpenAndMigrate(databasePath));
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
            return await commands.ExportAsync(rest[0], CancellationToken.None).ConfigureAwait(false);

        case "force-conclude" when rest.Length == 1:
            return await commands
                .ForceConcludeAsync(rest[0], DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);

        default:
            await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);
            return Usage;
    }
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
