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
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ParleyTools>();

var host = builder.Build();

try
{
    // Touches the session before serving, so a failure to open the database is reported here
    // rather than on the first tool call.
    _ = host.Services.GetRequiredService<BrokerSession>();
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Parley could not start: {ex.Message}").ConfigureAwait(false);
    return 74; // EX_IOERR.
}

await host.RunAsync().ConfigureAwait(false);

await host.Services.GetRequiredService<BrokerSession>().DisposeAsync().ConfigureAwait(false);

return 0;
