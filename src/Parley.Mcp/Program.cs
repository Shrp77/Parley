using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parley.Mcp;

// A Parley MCP server. One process, one agent, one project's database.
//
// Launched by an MCP client, which supplies the environment:
//
//   {
//     "mcpServers": {
//       "parley": {
//         "command": "parley-mcp",
//         "env": { "PARLEY_DB": "C:/proj/.parley/parley.db", "PARLEY_AGENT": "orders" }
//       }
//     }
//   }
//
// The agent's identity comes from that environment and from nowhere else. No tool takes it as
// a parameter, so no tool call can change who the caller is — see ServerOptions.

ServerOptions options;

try
{
    options = ServerOptions.FromEnvironment();
}
catch (InvalidOperationException ex)
{
    // Reported to stderr and exited, rather than surfaced through the protocol. A
    // misconfiguration belongs to whoever wrote the launch configuration, not to the model,
    // which can do nothing about it — and a client renders a process that exits with a message
    // far more usefully than a server whose every tool reports the same error.
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 78; // EX_CONFIG, which is what a supervisor reads to tell "wrong" from "broken".
}

var builder = Host.CreateApplicationBuilder(args);

// Logging goes to stderr, and this line is load-bearing rather than tidy. A stdio server speaks
// JSON-RPC over stdout, so anything else written there corrupts the stream — a single log line
// on the default console provider desynchronises the client, usually presenting as the server
// mysteriously failing to respond. It is the classic way to break one of these.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(options);

// Started eagerly rather than lazily, so a database that cannot be opened or migrated stops the
// launch instead of producing a server whose every tool fails the same way. The reboot sequence
// of SPEC.md §14 runs here too, which is what every broker operation refuses until.
builder.Services.AddSingleton(provider =>
    BrokerSession.StartAsync(
            provider.GetRequiredService<ServerOptions>(),
            TimeProvider.System)
        .GetAwaiter().GetResult());

builder.Services.AddHostedService<SweepService>();

builder.Services
    // Instructions travel with the protocol, which makes this the one way to tell a model what
    // Parley is for that does not depend on which vendor's client it lives in. See
    // ParleyInstructions for what belongs in it and what deliberately does not.
    .AddMcpServer(server => server.ServerInstructions = ParleyInstructions.Text)
    .WithStdioServerTransport()
    .WithTools<ParleyTools>();

var host = builder.Build();

BrokerSession session;

try
{
    // Touches the session before serving, so a failure to open the database is reported here
    // rather than on the first tool call.
    session = host.Services.GetRequiredService<BrokerSession>();
}
catch (AgentRetiredException ex)
{
    // Separated from the failures below because it is not a fault. An operator withdrew this
    // agent, and the launch configuration still names it — which is the same shape of problem
    // as a missing variable and gets the same exit code, so a supervisor can tell "configured
    // wrong" from "the machine is broken" without reading the message.
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 78; // EX_CONFIG.
}
#pragma warning disable CA1031 // Do not catch general exception types
catch (Exception ex)
#pragma warning restore CA1031
{
    // Broad on purpose. Whatever went wrong opening or migrating the database, the useful
    // behaviour is the same: say so on stderr and exit, so the client shows the person a
    // process that failed rather than a server that answers every tool with the same error.
    await Console.Error.WriteLineAsync($"Parley could not start: {ex.Message}").ConfigureAwait(false);
    return 74; // EX_IOERR.
}

try
{
    await host.RunAsync().ConfigureAwait(false);
}
finally
{
    // In a finally, so the agent is marked offline even when serving ends badly. The mark is
    // cosmetic — nothing routes on it — but a registry that reports everybody live is a registry
    // nobody consults, and this is the only moment the process knows it is stopping.
    await session.DisposeAsync().ConfigureAwait(false);
}

return 0;
