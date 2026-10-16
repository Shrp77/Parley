using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Parley.Server;

/// <summary>
/// The broker listening on a socket: SPEC.md §4's one process, hosted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plaintext HTTP/2, and no credentials.</b> SPEC.md §16 records a hostile network as a
/// non-goal, and inventing authentication here would contradict the document rather than
/// implement it — worse, it would look like a control while §16 explains at length why the
/// protocol offers none: anything that reaches the broker can claim to be any agent, and the
/// name in a request is a routing address rather than an assertion about who is calling.
/// The deployment boundary is the network, and that is a decision to argue with in the spec.
/// </para>
/// <para>
/// <b>Local is not a special case.</b> An agent on the broker's own machine connects over
/// loopback, through this same listener and the same service code, as one on another machine
/// does. A short cut for local callers would be a second implementation of every operation and
/// a second way for them to disagree — and the disagreement would show up only in the
/// deployment that had both.
/// </para>
/// </remarks>
public sealed class ParleyServer : IAsyncDisposable
{
    private readonly WebApplication _application;

    private ParleyServer(WebApplication application, BrokerRuntime runtime)
    {
        _application = application;
        Runtime = runtime;
    }

    /// <summary>Gets the broker state this server is serving.</summary>
    public BrokerRuntime Runtime { get; }

    /// <summary>Gets the addresses the server is listening on.</summary>
    /// <value>
    /// Resolved after startup, so a server asked to listen on port zero reports the port it was
    /// actually given. That is what makes a test able to bind a real socket without choosing a
    /// number and hoping.
    /// </value>
    public IReadOnlyList<string> Addresses =>
        [.. _application.Urls];

    /// <summary>
    /// Starts a broker on an endpoint.
    /// </summary>
    /// <param name="runtime">The broker state to serve. The server disposes it.</param>
    /// <param name="listenOn">
    /// The address, as a URL. Use port zero to be assigned one, and read
    /// <see cref="Addresses"/> afterwards.
    /// </param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The running server.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<ParleyServer> StartAsync(
        BrokerRuntime runtime,
        string listenOn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrEmpty(listenOn);

        var builder = WebApplication.CreateSlimBuilder();

        // Quiet by default. A broker is a background service and its ordinary operation is not
        // news; what a person wants to see is what the CLI reports when they ask. Warnings and
        // above still surface, because a refused bind or a failed migration is news.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(options =>
        {
            // HTTP/2 only, without TLS. gRPC needs HTTP/2, and Kestrel will not negotiate up to
            // it on an unencrypted connection -- so it has to be told, rather than left to fall
            // back to HTTP/1.1 and fail every call with a protocol error that names neither
            // cause nor remedy.
            options.ConfigureEndpointDefaults(endpoint =>
                endpoint.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(runtime);

        var application = builder.Build();

        application.Urls.Clear();
        application.Urls.Add(listenOn);

        application.MapGrpcService<AgentsService>();
        application.MapGrpcService<OperatorsService>();
        application.MapGrpcService<NodesService>();

        // Every held stream is closed before Kestrel begins waiting for its handlers, and this
        // is not tidiness: an Attach handler sits in a read that completes only when its channel
        // does, so a graceful shutdown with agents attached waits the full ShutdownTimeout.
        //
        // What a person sees is Ctrl+C appearing to hang, and what a service manager sees is a
        // process that missed its stop deadline and has to be killed. Closing the channels here
        // ends each handler in the ordinary way, so the wait is over as soon as they return.
        application.Lifetime.ApplicationStopping.Register(runtime.CloseStreams);

        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        return new ParleyServer(application, runtime);
    }

    /// <summary>Stops listening and closes the database.</summary>
    /// <returns>A task that completes when both are done.</returns>
    /// <remarks>
    /// The listener first. Every attached agent's stream ends when it stops, and each of those
    /// wants to write a detach against the connection — so closing the database first would race
    /// every one of them.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        await Runtime.DisposeAsync().ConfigureAwait(false);
    }
}
