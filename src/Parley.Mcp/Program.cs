using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parley.Mcp;

// A Parley MCP server. One process, one agent, one connection to the broker.
//
// Launched by an MCP client, which supplies the environment:
//
//   {
//     "mcpServers": {
//       "parley-acme": {
//         "command": "parley-mcp",
//         "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "acme" }
//       }
//     }
//   }
//
// The key is parley-<project>, or the bare "parley" when the deployment names no projects. One
// repository may hold an entry per project it answers in; this process is launched once per entry
// and speaks for exactly one of them. Nothing here reads the key — the identity is the
// environment — but an illustration that disagreed with the tool would be read by the person who
// has not run the tool yet.
//
// No database path. This process opens no store and could not if it were told to: SPEC.md §4
// gives the file to one broker process, and everything else reaches it over the transport. The
// broker is found at PARLEY_BROKER, or on loopback if that says nothing.
//
// The agent's identity comes from that environment and from nowhere else. No tool takes it as
// a parameter, so no tool call can change who the caller is — see ServerOptions.

// Before the environment is read, because asking which build this is must not need a launch configuration. The
// installer asks every tool it installed, from a shell that sets nothing -- and while this answered only with
// PARLEY_AGENT set, its check printed nothing and passed.
if (Array.Exists(args, argument => argument is "--version" or "-v"))
{
    var build = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ServerOptions).Assembly)
        ?.InformationalVersion ?? "unknown";
    var plus = build.IndexOf('+', StringComparison.Ordinal);

    await Console.Out.WriteLineAsync(string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"parley-mcp {(plus < 0 ? build : build[..plus])}, speaking Parley protocol {Parley.Core.ProtocolVersion.Current}"))
        .ConfigureAwait(false);

    return 0;
}

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

// Said rather than silently applied. A launch configuration carrying both a project and an
// address is somebody's half-finished edit, and the endpoint winning quietly is how an agent
// ends up in the wrong deployment with nothing anywhere that looks wrong.
if (options.BrokerNotice is { Length: > 0 } notice)
{
    await Console.Error.WriteLineAsync(notice).ConfigureAwait(false);
}

var builder = Host.CreateApplicationBuilder(args);

// Logging goes to stderr, and this line is load-bearing rather than tidy. A stdio server speaks
// JSON-RPC over stdout, so anything else written there corrupts the stream — a single log line
// on the default console provider desynchronises the client, usually presenting as the server
// mysteriously failing to respond. It is the classic way to break one of these.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(options);

// Started eagerly rather than lazily, so a broker that cannot be reached stops the launch
// instead of producing a server whose every tool fails the same way. The attach sequence of
// SPEC.md §14 runs here too, which is what every broker operation refuses until.
builder.Services.AddSingleton(provider =>
    BrokerLink.StartAsync(provider.GetRequiredService<ServerOptions>())
        .GetAwaiter().GetResult());

// No sweep here any more. It ran in every adapter while every adapter opened the store, which
// meant a deployment of twenty repositories ran twenty sweeps over one file, each taking the
// write lock for the same work. It belongs to the process that owns the store.

builder.Services
    // Instructions travel with the protocol, which makes this the one way to tell a model what
    // Parley is for that does not depend on which vendor's client it lives in. See
    // ParleyInstructions for what belongs in it and what deliberately does not.
    .AddMcpServer(server => server.ServerInstructions = ParleyInstructions.Text)
    .WithStdioServerTransport()
    .WithTools<ParleyTools>();

var host = builder.Build();

BrokerLink link;

try
{
    // Touches the link before serving, so a broker that is not running is reported here rather
    // than on the first tool call.
    link = host.Services.GetRequiredService<BrokerLink>();
}
catch (Grpc.Core.RpcException ex)
{
    // The status detail, not the exception message. RpcException's own message wraps the
    // sentence in Status(StatusCode="FailedPrecondition", Detail="...") -- machine-readable
    // framing around the one part a person needs, printed to a console where the framing is
    // noise. The broker's refusals are written for whoever is reading this line: a protocol
    // mismatch, a retired agent, a name another repository holds.
    await Console.Error.WriteLineAsync($"Parley could not start: {ex.Status.Detail}")
        .ConfigureAwait(false);

    return 78; // EX_CONFIG: something in the deployment needs changing, not retrying.
}
catch (BrokerUnreachableException ex)
{
    // The failure a new deployment meets first, and it gets its own arm because its remedy is
    // its own: nothing is misconfigured and nothing is broken, there is simply no broker running
    // yet. The message says how to start one.
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 69; // EX_UNAVAILABLE: the service this depends on is not up.
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
    // Broad on purpose. Whatever went wrong reaching or attaching to the broker, the useful
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
    // In a finally, so the connection is closed even when serving ends badly. Closing it is what
    // says this agent has gone (§4.0) — and while an abrupt exit says the same thing by breaking
    // the socket, a clean close says it immediately rather than whenever the broker notices.
    await link.DisposeAsync().ConfigureAwait(false);
}

return 0;
