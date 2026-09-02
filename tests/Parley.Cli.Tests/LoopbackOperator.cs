using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// A broker on a real socket, over a connection the test opened.
/// </summary>
/// <remarks>
/// <para>
/// These tests seed a database and then check what the operator's commands print about it, and
/// that arrangement survived the transport intact — but only because the runtime can be handed a
/// connection rather than opening one. An in-memory database lives inside its connection, so a
/// test that seeded one connection and served another would be checking reports about an empty
/// database and passing every assertion that expected nothing.
/// </para>
/// <para>
/// A real listener rather than a direct call into the service classes. The operator's tool is a
/// client now, and the questions worth asking of it — does a refusal reach the terminal as a
/// sentence, does a report survive serialisation — are all about the wire.
/// </para>
/// </remarks>
internal sealed class LoopbackOperator : IAsyncDisposable
{
    private readonly ParleyServer _server;
    private readonly GrpcChannel _channel;

    private LoopbackOperator(ParleyServer server, GrpcChannel channel)
    {
        _server = server;
        _channel = channel;

        Operators = new Wire.Operators.OperatorsClient(channel);
        Agents = new Wire.Agents.AgentsClient(channel);
    }

    /// <summary>Where this broker is listening.</summary>
    /// <remarks>
    /// Exposed for the tests that build their own client rather than borrowing the one above --
    /// a surface under test opens its own channels, and handing it a ready-made one would test a
    /// connection this class made instead of the connection the surface makes.
    /// </remarks>
    public string Address => _server.Addresses[0];

    /// <summary>The operator surface of SPEC.md §10.2, over the wire.</summary>
    public Wire.Operators.OperatorsClient Operators { get; }

    /// <summary>The agent surface, for tests that need to put something there first.</summary>
    public Wire.Agents.AgentsClient Agents { get; }

    /// <summary>Starts a broker over a connection the caller owns.</summary>
    /// <param name="connection">The open, migrated, already-seeded connection.</param>
    /// <param name="retention">What the broker prunes to, or null for the default.</param>
    /// <param name="clock">The clock, or null for the system one.</param>
    /// <returns>The running broker.</returns>
    /// <remarks>
    /// Synchronous because every test class here constructs its fixture in a constructor, and a
    /// constructor cannot await. The block is on a loopback listener starting, which is
    /// milliseconds.
    /// </remarks>
    public static LoopbackOperator Start(
        SqliteConnection connection,
        RetentionPolicy? retention = null,
        TimeProvider? clock = null)
    {
        var runtime = BrokerRuntime.Over(
            connection, BrokerOptions.Default, clock ?? TimeProvider.System, retention);

        var server = ParleyServer.StartAsync(runtime, "http://127.0.0.1:0")
            .GetAwaiter().GetResult();

        return new LoopbackOperator(server, GrpcChannel.ForAddress(server.Addresses[0]));
    }

    /// <summary>Stops the broker and closes the client.</summary>
    /// <returns>A task that completes when both are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _channel.ShutdownAsync().ConfigureAwait(false);
        _channel.Dispose();

        // Disposes the runtime, which leaves the caller's connection open — see BrokerRuntime.Over.
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A clock the tests move by hand.
/// </summary>
/// <remarks>
/// A stub rather than <c>FakeTimeProvider</c>, which would mean a package on the test path for
/// two members. It exists because retention is measured against the broker's clock now: a prune
/// test used to pass the instant it wanted, and the instant is the broker's to read.
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
