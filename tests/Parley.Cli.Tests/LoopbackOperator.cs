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

    private LoopbackOperator(ParleyServer server, GrpcChannel channel, BrokerRuntime runtime)
    {
        _server = server;
        _channel = channel;
        Runtime = runtime;

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

    /// <summary>The broker itself, for tests that change its file location as another program would and then sweep.</summary>
    public BrokerRuntime Runtime { get; }

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

        return new LoopbackOperator(server, GrpcChannel.ForAddress(server.Addresses[0]), runtime);
    }

    /// <summary>How long a teardown waits for the channel before it stops waiting and says so.</summary>
    /// <remarks>
    /// Generous, because the honest case is milliseconds and the only thing this bound exists to catch is a
    /// call that is never going to complete.
    /// </remarks>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(10);

    /// <summary>Stops the broker and closes the client.</summary>
    /// <returns>A task that completes when both are closed.</returns>
    /// <remarks>
    /// <para>
    /// <b>The channel shutdown is bounded so that a teardown which never returns is reported rather than
    /// waited on.</b> <c>ShutdownAsync</c> waits for pending calls and took no deadline, and a seat's mail
    /// poll runs every five seconds by default (<c>Shell.MailEvery</c>), so there is routinely a call in
    /// flight when a fixture is torn down.
    /// </para>
    /// <para>
    /// <b>What makes that worth a bound is the gate rather than the wait.</b> <c>Driving.Open</c> admits one
    /// driven test at a time across the whole assembly, so a class whose teardown never returns never
    /// releases it and every remaining test queues behind it. The suite would then end with no failure, no
    /// message and no summary — a shape indistinguishable from a slow machine, which is the expensive part:
    /// telling those two apart in this project has cost dump captures and hours of waiting.
    /// </para>
    /// <para>
    /// <b>This bound has never fired, and it is not a fix for anything observed.</b> It was added while
    /// chasing a Terminal suite that would not finish; that turned out to be contention from overlapping
    /// runs, and an undisturbed suite completes in its usual time with no timeout here. It is kept for the
    /// failure it names, not for one it caught — and if it ever does fire, the thing to doubt first is
    /// whether a surface outlived the fixture, because the product's own wiring is sound: the poll is
    /// linked to the surface's token and <c>MailOnceAsync</c> passes it to the call.
    /// </para>
    /// <para>
    /// <b>Bounded and loud rather than bounded and quiet.</b> Swallowing the timeout would turn an endless
    /// wait into a silent one, which is the same defect wearing a disguise.
    /// </para>
    /// </remarks>
    /// <exception cref="TimeoutException">The channel did not shut down within the budget.</exception>
    public async ValueTask DisposeAsync()
    {
        var closing = _channel.ShutdownAsync();

        // The delay is cancelled on the ordinary path. Left running it would hold a timer per teardown for
        // the whole budget, and this fixture is disposed several hundred times in one run.
        using (var expiry = new CancellationTokenSource())
        {
            var lapsed = Task.Delay(ShutdownBudget, expiry.Token);

            if (await Task.WhenAny(closing, lapsed).ConfigureAwait(false) != closing)
            {
                throw new TimeoutException(
                    $"The loopback channel did not shut down within {ShutdownBudget.TotalSeconds:0} seconds. "
                    + "Something is still calling the broker this fixture is tearing down — a seat's mail poll "
                    + "or a transcript follow that outlived the surface that started it.");
            }

            await expiry.CancelAsync().ConfigureAwait(false);
        }

        await closing.ConfigureAwait(false);

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
/// two members. It exists because retention is measured against the broker's clock: a prune
/// test cannot pass the instant it wants, because the instant is the broker's to read.
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
