using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;

namespace Parley.Mcp.Tests;

/// <summary>
/// A clock the tests move by hand.
/// </summary>
/// <remarks>
/// A stub rather than <c>FakeTimeProvider</c>, which would mean a package on the test path for
/// two members. It belongs to the broker now rather than to each server: the adapter reads no
/// clock at all, because every instant a message carries is stamped by the process that stores it.
/// </remarks>
internal sealed class StubClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>The instant every read returns.</summary>
    public DateTimeOffset Now { get; set; } = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// One broker on a loopback socket, and the adapters attached to it.
/// </summary>
/// <remarks>
/// <para>
/// This used to be one temporary database file and the sessions that opened it, and the file was
/// the point: two agents on one project share a store, and every question about whether two
/// servers can see each other's work is a question about the file — an in-memory database is
/// private to its connection, so two sessions over one would have passed every test here while
/// sharing nothing.
/// </para>
/// <para>
/// The argument survives and its conclusion inverts. What two agents now share is a
/// <em>broker</em>, and one broker holds one connection — so an in-memory database is exactly
/// right, and the thing that would make these tests vacuous is starting two brokers rather than
/// opening two connections. The file is gone and so is the temporary directory that had to be
/// cleaned up after it.
/// </para>
/// <para>
/// The listener is real. An in-process shortcut would test the tool layer and skip the half of
/// the system these tests are now about: whether an agent's operations survive being serialised,
/// and whether the broker can tell two attached agents apart.
/// </para>
/// </remarks>
internal sealed class ServerFixture : IAsyncDisposable
{
    private readonly List<BrokerLink> _links = [];
    private readonly List<GrpcChannel> _channels = [];
    private readonly SqliteConnection _connection;
    private readonly ParleyServer _server;
    private bool _stopped;

    public ServerFixture(DateTimeOffset start)
    {
        Clock = new StubClock(start);

        _connection = ParleyDatabase.OpenInMemory();

        var runtime = BrokerRuntime.Over(_connection, BrokerOptions.Default, Clock);

        _server = ParleyServer.StartAsync(runtime, "http://127.0.0.1:0")
            .GetAwaiter().GetResult();

        Broker = _server.Addresses[0];
    }

    /// <summary>The broker every server in this fixture connects to.</summary>
    public string Broker { get; }

    /// <summary>The clock the broker stamps operations from.</summary>
    public StubClock Clock { get; }

    /// <summary>
    /// The broker's connection, for a test that needs to look behind the wire.
    /// </summary>
    /// <remarks>
    /// Reaching around the transport is exactly what production code may not do, and a test that
    /// checks what was stored has to. Kept to reads and seeding: a test that drove the protocol
    /// from here would be testing the broker, which its own suite already does.
    /// </remarks>
    public SqliteConnection Connection => _connection;

    /// <summary>Builds the launch configuration for one agent.</summary>
    /// <param name="agent">The agent the server speaks for.</param>
    /// <param name="responder">What to attribute its messages to.</param>
    /// <param name="owns">What its launch configuration says it owns, if anything.</param>
    /// <returns>The options.</returns>
    public ServerOptions OptionsFor(
        string agent, string responder = "test-model", string? owns = null) =>
        new(Broker,
            AgentName.Create(agent.Contains('@', StringComparison.Ordinal)
                ? agent
                : agent + "@" + MachineName.Current),
            responder,
            null,
            owns);

    /// <summary>Launches a server for an agent, as its client would.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <param name="owns">What its launch configuration says it owns, if anything.</param>
    /// <returns>The attached link and the tools bound to it.</returns>
    public async Task<(BrokerLink Link, ParleyTools Tools)> LaunchAsync(
        string agent, CancellationToken cancellationToken, string? owns = null)
    {
        var options = OptionsFor(agent, owns: owns);
        var link = await BrokerLink.StartAsync(options, cancellationToken);

        _links.Add(link);

        return (link, new ParleyTools(link, options));
    }

    /// <summary>Closes a server as its client closing the pipe would.</summary>
    /// <param name="link">The link to close.</param>
    /// <returns>A task that completes when it is closed.</returns>
    public async Task ShutDownAsync(BrokerLink link)
    {
        _links.Remove(link);
        await link.DisposeAsync();
    }

    /// <summary>Runs the broker's sweep, as its own process does on a timer.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    /// <remarks>
    /// Driven from the test rather than left to a timer, and reached through the runtime rather
    /// than through an agent's link. The sweep is the broker's own work now — SPEC.md §4 gives a
    /// deployment one of them — so an adapter has no way to ask for one, which is why a test that
    /// used to call it through a session calls it here.
    /// </remarks>
    public Task TickAsync(CancellationToken cancellationToken) =>
        _server.Runtime.TickAsync(cancellationToken);

    /// <summary>Runs the sweep and reports what it did.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass orphaned, ghosted and pruned.</returns>
    public Task<TickResult> SweepAsync(CancellationToken cancellationToken) =>
        _server.Runtime.TickAsync(cancellationToken);

    /// <summary>Stops the broker while the agents stay running.</summary>
    /// <returns>A task that completes when it has stopped listening.</returns>
    /// <remarks>
    /// What an upgrade looks like from an agent's side: the process it depends on goes away and
    /// it does not. Nothing else in this fixture can produce that state, and it is the failure
    /// the transport introduced — so it is worth being able to reach deliberately.
    /// </remarks>
    public async Task StopBrokerAsync()
    {
        await _server.DisposeAsync();
        _stopped = true;
    }

    /// <summary>Opens a raw client, for tests that need one without an attachment.</summary>
    /// <returns>The client.</returns>
    public Transport.Agents.AgentsClient Connect()
    {
        var channel = GrpcChannel.ForAddress(Broker);
        _channels.Add(channel);

        return new Transport.Agents.AgentsClient(channel);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var link in _links)
        {
            await link.DisposeAsync();
        }

        _links.Clear();

        foreach (var channel in _channels)
        {
            await channel.ShutdownAsync();
            channel.Dispose();
        }

        _channels.Clear();

        // The server before the connection it borrowed: stopping it ends every attach stream,
        // and each of those writes a detach. Skipped when a test stopped it already, because
        // disposing twice would close the connection twice.
        if (!_stopped)
        {
            await _server.DisposeAsync();
        }

        _connection.Dispose();
    }
}
