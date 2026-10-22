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
/// Two agents on one project share a store, and every question about whether two
/// servers can see each other's work is a question about what they share. What two agents share is a
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

    /// <summary>Launches a server for an agent whose repository is a given directory.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="workspace">The repository's root, which the file tools are confined to.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>The attached link and the tools bound to it.</returns>
    /// <remarks>
    /// The file tools read and write inside the workspace, and fall back to the current directory without one — which
    /// here would be the test run's own output directory.
    /// </remarks>
    public async Task<(BrokerLink Link, ParleyTools Tools)> LaunchInAsync(
        string agent, string workspace, CancellationToken cancellationToken)
    {
        var options = OptionsFor(agent) with { Workspace = workspace };
        var link = await BrokerLink.StartAsync(options, cancellationToken);

        _links.Add(link);

        return (link, new ParleyTools(link, options));
    }

    /// <summary>Closes a server as its client closing the pipe would.</summary>
    /// <param name="link">The link to close.</param>
    /// <returns>A task that completes when the broker has seen it go.</returns>
    /// <exception cref="TimeoutException">The broker never noticed the stream end.</exception>
    /// <remarks>
    /// <para>
    /// Waits for the broker, not just for the client. Disposing the link ends the attach stream
    /// on this side; the broker releases the session when its handler unwinds, which is a
    /// separate event on a separate thread. A test that relaunched the same agent straight
    /// after this returned was racing the two, and when the relaunch won, the agent had never
    /// been absent at all: two sessions for one name are ordinary (§14), so nothing was released
    /// and the answer the test expected refused was accepted. One run in thirty on a loaded
    /// Linux box, never on Windows.
    /// </para>
    /// <para>
    /// The same lesson is written up twice in the Server tests' loopback broker, once for agents
    /// and once for nodes. This is the third copy, and it waits on the session count rather than
    /// on the name because a second-session test closes one of two windows and the name is
    /// supposed to stay.
    /// </para>
    /// </remarks>
    public async Task ShutDownAsync(BrokerLink link)
    {
        _links.Remove(link);

        var before = _server.Runtime.SessionsFor(link.Agent);

        await link.DisposeAsync();

        if (before == 0)
        {
            return;
        }

        var within = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + within;

        while (_server.Runtime.SessionsFor(link.Agent) >= before)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"'{link.Agent.Value}' still had {before} session(s) attached {within} " +
                    "after its stream ended. The broker did not notice the disconnect.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Runs the broker's sweep, as its own process does on a timer.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    /// <remarks>
    /// Driven from the test rather than left to a timer, and reached through the runtime rather
    /// than through an agent's link. The sweep is the broker's own work — SPEC.md §4 gives a
    /// deployment one of them — so an adapter has no way to ask for one, which is why a test
    /// calls it here and not through a session.
    /// </remarks>
    public Task TickAsync(CancellationToken cancellationToken) =>
        _server.Runtime.TickAsync(cancellationToken);

    /// <summary>Runs the sweep and reports what it did.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass concluded and pruned.</returns>
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
