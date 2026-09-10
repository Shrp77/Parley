using Grpc.Core;
using Grpc.Net.Client;
using Parley.Broker;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A broker on a real socket, and clients that reach it the way a deployment's would.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real listener, deliberately.</b> An in-process test double for the transport would test
/// the service classes and nothing else — and the interesting failures in a transport are in the
/// parts a double replaces: whether a stream ending is noticed, whether cancellation reaches the
/// handler, whether a message survives being serialised. Loopback on an assigned port costs a
/// few milliseconds per class and exercises all of it.
/// </para>
/// <para>
/// Port zero, so nothing chooses a number. Tests run in parallel and a fixed port is a fixed
/// collision waiting for the day two classes run together.
/// </para>
/// </remarks>
internal sealed class LoopbackBroker : IAsyncDisposable
{
    private readonly ParleyServer _server;
    private readonly List<GrpcChannel> _channels = [];

    private LoopbackBroker(ParleyServer server, TimeProvider clock)
    {
        _server = server;
        Clock = clock;
    }

    /// <summary>Gets the clock the broker stamps operations from.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Gets the runtime, for tests that need to look behind the wire.</summary>
    public BrokerRuntime Runtime => _server.Runtime;

    /// <summary>Starts a broker over an in-memory store.</summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <param name="instructionPatience">
    /// How long the broker waits for a turn before answering that it is still running, or null for
    /// the shipped default.
    /// </param>
    /// <param name="clock">The clock to stamp from, or null for the system one.</param>
    /// <returns>The running broker.</returns>
    public static async Task<LoopbackBroker> StartAsync(
        CancellationToken cancellationToken,
        TimeSpan? instructionPatience = null,
        TimeProvider? clock = null)
    {
        // **Settable for the same reason the patience above is.** The wake ceiling is two minutes,
        // and a test that waited it out would be deleted by whoever next ran the suite -- so the
        // case that matters, a wake that never produced a drain, would be the one nothing could
        // reach. Defaults to the system clock, so every existing test is unaffected.
        clock ??= TimeProvider.System;

        // **The wait is settable so a test can watch it elapse, which is the path that used to
        // destroy an agent's answer.** An operator who gave up cancelled the settling write and
        // kept the reply; an operator who waited got `failed` written first and lost it. Only the
        // second is the defect, and at the shipped thirty seconds no test could sit through it —
        // so the case that mattered was the one nothing could reach.
        var options = instructionPatience is { } patience
            ? BrokerOptions.Default with { InstructionPatience = patience }
            : BrokerOptions.Default;

        var runtime = BrokerRuntime.OpenInMemory(options, clock);

        var server = await ParleyServer
            .StartAsync(runtime, "http://127.0.0.1:0", cancellationToken)
            .ConfigureAwait(false);

        return new LoopbackBroker(server, clock);
    }

    /// <summary>
    /// Waits for the broker to notice that an agent's stream has ended.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="within">How long to wait before giving up.</param>
    /// <returns>A task that completes when it is gone.</returns>
    /// <exception cref="TimeoutException">It was still attached.</exception>
    /// <remarks>
    /// <para>
    /// Disposing a client cancels its call and returns; the server removes the session when its
    /// handler unwinds, which is a separate event on a separate thread. A test that asserted
    /// immediately after disposing was racing the two, and lost about one run in eight.
    /// </para>
    /// <para>
    /// The wait is bounded rather than removed, because the property is worth keeping: SPEC.md
    /// §4.0 says a stream ending is an agent going away, and "eventually, within a moment" is
    /// what that means in a system where the two sides are different processes. What would be
    /// wrong is asserting it happens synchronously, which is a promise the transport does not
    /// make and does not need to.
    /// </para>
    /// </remarks>
    public Task WaitUntilDetachedAsync(string agent, TimeSpan within) =>
        WaitUntilGoneAsync(() => Runtime.Attached.Contains(agent), agent, "attached", within);

    /// <summary>Waits until the broker has noticed a node's stream ended.</summary>
    /// <param name="machine">The machine that was joined.</param>
    /// <param name="within">How long to allow.</param>
    /// <returns>A task that completes once the node is gone.</returns>
    /// <exception cref="TimeoutException">It was still listed when the time ran out.</exception>
    /// <remarks>
    /// The node half of <see cref="WaitUntilDetachedAsync"/>, and it is here because the lesson
    /// that method records was learned twice. A node test asserted its machine was gone the
    /// instant after disposing the client, which races the server-side handler unwinding on
    /// another thread — it held on Windows and lost roughly one run in three on Linux, which is
    /// the worst version of the fault: reliably green on the machine most people develop on.
    /// </remarks>
    public Task WaitUntilNodeGoneAsync(string machine, TimeSpan within) =>
        WaitUntilGoneAsync(() => Runtime.Nodes.Contains(machine), machine, "listed", within);

    /// <summary>Polls until the broker has forgotten something, or gives up.</summary>
    /// <param name="stillThere">Says whether the broker still holds it.</param>
    /// <param name="what">What is being waited on, for the message.</param>
    /// <param name="state">The word for still being there: "attached", "listed".</param>
    /// <param name="within">How long to allow.</param>
    /// <returns>A task that completes when it is gone.</returns>
    /// <exception cref="TimeoutException">It was still there when the time ran out.</exception>
    /// <remarks>
    /// One loop, two names. The callers had a copy each and the copies were identical, which is
    /// the state this repository has already been bitten by — two copies of its repository-file
    /// helper existed within a day of each other and had begun to differ. A duplicated wait is
    /// worse than most duplication, because the half that drifts does not fail: it waits
    /// differently, passes anyway, and stops passing only on somebody else's machine.
    /// </remarks>
    private static async Task WaitUntilGoneAsync(
        Func<bool> stillThere, string what, string state, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;

        while (stillThere())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"'{what}' was still {state} {within} after its stream ended. The broker " +
                    "did not notice the disconnect.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a client channel to this broker.</summary>
    /// <returns>The client.</returns>
    public Wire.Agents.AgentsClient Connect() =>
        new(Channel());

    /// <summary>Opens an operator client.</summary>
    /// <returns>The client.</returns>
    public Wire.Operators.OperatorsClient ConnectAsOperator() =>
        new(Channel());

    /// <summary>Opens a node client.</summary>
    /// <returns>The client.</returns>
    public Wire.Nodes.NodesClient ConnectAsNode() =>
        new(Channel());

    private GrpcChannel Channel()
    {
        var channel = GrpcChannel.ForAddress(_server.Addresses[0]);
        _channels.Add(channel);

        return channel;
    }

    /// <summary>Stops the broker and closes every client.</summary>
    /// <returns>A task that completes when everything is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
        {
            await channel.ShutdownAsync().ConfigureAwait(false);
            channel.Dispose();
        }

        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// One agent's side of the wire: its client, its attach stream, and the notices on it.
/// </summary>
/// <remarks>
/// The attach stream is read on its own task, exactly as an adapter would have to read it. A test
/// that read it inline could only observe a notice it was already expecting, in the order it
/// expected — which is the shape of test that passes while the push does nothing.
/// </remarks>
internal sealed class AttachedClient : IAsyncDisposable
{
    private readonly AsyncServerStreamingCall<Wire.Event> _stream;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;
    private readonly SemaphoreSlim _arrived = new(0);

    private AttachedClient(
        Wire.Agents.AgentsClient client,
        string name,
        AsyncServerStreamingCall<Wire.Event> stream,
        Wire.Attached attached)
    {
        Client = client;
        Name = name;
        Attached = attached;
        _stream = stream;

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Gets the client this agent calls through.</summary>
    public Wire.Agents.AgentsClient Client { get; }

    /// <summary>Gets the agent's name.</summary>
    public string Name { get; }

    /// <summary>Gets what the attach sequence reported.</summary>
    public Wire.Attached Attached { get; }

    /// <summary>Gets every notice received, in order.</summary>
    public List<Wire.Notice> Notices { get; } = [];

    /// <summary>Attaches an agent and waits for its first event.</summary>
    /// <param name="broker">The broker.</param>
    /// <param name="name">The agent name.</param>
    /// <param name="workspace">Where it is launching from, or null.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The attached client.</returns>
    public static async Task<AttachedClient> AttachAsync(
        LoopbackBroker broker,
        string name,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        var client = broker.Connect();

        var stream = client.Attach(
            new Wire.AttachRequest
            {
                Agent = name,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Workspace = workspace ?? string.Empty,

                // Announced, because the broker checks it. Taken from the constant rather than
                // written out, so a version bump does not need this file edited -- and so the
                // check cannot be satisfied by a literal that happens to match.
                ProtocolVersion = Parley.Core.ProtocolVersion.Current
            },
            cancellationToken: cancellationToken);

        // The first event is always the attach result, per the contract. Awaited here so that a
        // test which goes straight to asking is not racing the registration behind it.
        if (!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
            || stream.ResponseStream.Current.EventCase != Wire.Event.EventOneofCase.Attached)
        {
            throw new InvalidOperationException(
                "The first event on an attach stream was not the attach result.");
        }

        return new AttachedClient(client, name, stream, stream.ResponseStream.Current.Attached);
    }

    /// <summary>Waits for a notice that says something is waiting.</summary>
    /// <param name="within">How long to wait before giving up.</param>
    /// <returns>The notice.</returns>
    /// <exception cref="TimeoutException">None arrived.</exception>
    /// <remarks>
    /// <para>
    /// A bounded wait rather than a poll. The point of the held connection is that the broker
    /// speaks first, so a test that polled would be measuring its own loop rather than the push.
    /// </para>
    /// <para>
    /// <b>Empty notices are skipped, and that is not the test being lenient.</b> Attaching sends
    /// one immediately so an agent that comes back to a full inbox is told at once — and when
    /// the inbox is empty that notice says zero. A wait that took the first thing to arrive
    /// would therefore return the attach's own notice and assert against it, which passes or
    /// fails depending on how the scheduler interleaved two attaches. It failed roughly one full
    /// run in four, and passed every time in isolation.
    /// </para>
    /// </remarks>
    public Task<Wire.Notice> WaitForNoticeAsync(TimeSpan within) =>
        WaitForNoticeAsync(within, notice => notice.MessagesWaiting > 0);

    /// <summary>Waits for a notice matching a condition.</summary>
    /// <param name="within">How long to wait before giving up.</param>
    /// <param name="until">What makes a notice the one being waited for.</param>
    /// <returns>The notice.</returns>
    /// <exception cref="TimeoutException">None arrived.</exception>
    public async Task<Wire.Notice> WaitForNoticeAsync(TimeSpan within, Func<Wire.Notice, bool> until)
    {
        ArgumentNullException.ThrowIfNull(until);

        var deadline = DateTime.UtcNow + within;

        while (true)
        {
            lock (Notices)
            {
                for (var i = Notices.Count - 1; i >= 0; i--)
                {
                    if (until(Notices[i]))
                    {
                        return Notices[i];
                    }
                }
            }

            var left = deadline - DateTime.UtcNow;

            if (left <= TimeSpan.Zero || !await _arrived.WaitAsync(left).ConfigureAwait(false))
            {
                throw new TimeoutException(
                    $"No matching notice reached '{Name}' within {within}. The broker did not " +
                    "speak first.");
            }
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var received in _stream.ResponseStream
                .ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                if (received.EventCase != Wire.Event.EventOneofCase.Notice)
                {
                    continue;
                }

                lock (Notices)
                {
                    Notices.Add(received.Notice);
                }

                _ = _arrived.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The ordinary end of an attachment.
        }
        catch (RpcException)
        {
            // The server went away, which for a test is a teardown rather than a fault.
        }
    }

    /// <summary>Ends the attachment, which is what tells the registry the agent has gone.</summary>
    /// <returns>A task that completes when the stream has closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        // The pump first, then the call. Disposing the call is what tears down the underlying
        // HTTP/2 request, and the pump is sitting inside a read on it -- so the other order
        // races, and the read observes a disposed call rather than a cancelled one.
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the pump was cancelled above.
        }

        _stream.Dispose();
        _cancellation.Dispose();
        _arrived.Dispose();
    }
}

/// <summary>
/// One machine's side of the wire: its node client, its join stream, and the commands on it.
/// </summary>
internal sealed class JoinedNodeClient : IAsyncDisposable
{
    private readonly AsyncServerStreamingCall<Wire.NodeCommand> _stream;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly List<Wire.NodeCommand> _commands = [];

    private JoinedNodeClient(
        Wire.Nodes.NodesClient nodes,
        string machine,
        AsyncServerStreamingCall<Wire.NodeCommand> stream)
    {
        Nodes = nodes;
        Machine = machine;
        _stream = stream;

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Gets the client this node calls through.</summary>
    public Wire.Nodes.NodesClient Nodes { get; }

    /// <summary>Gets the machine name.</summary>
    public string Machine { get; }

    /// <summary>Joins a machine and waits until the broker has recorded it.</summary>
    /// <param name="broker">The broker.</param>
    /// <param name="machine">The machine name.</param>
    /// <param name="startable">The roles this node can launch.</param>
    /// <param name="cancellationToken">Cancels the join.</param>
    /// <param name="facts">What the node observed about itself.</param>
    /// <returns>The joined node.</returns>
    public static async Task<JoinedNodeClient> JoinAsync(
        LoopbackBroker broker,
        string machine,
        IEnumerable<string> startable,
        CancellationToken cancellationToken = default,
        Wire.NodeFacts? facts = null)
    {
        var nodes = broker.ConnectAsNode();

        var request = new Wire.JoinRequest
        {
            Machine = machine,
            Facts = facts ?? new Wire.NodeFacts { Os = "test-os" }
        };

        request.StartableRoles.AddRange(startable);

        var stream = nodes.Join(request, cancellationToken: cancellationToken);

        // The first command is always the acknowledgement, per the contract. Awaited here so a
        // test that joins and immediately asks a question is not racing the registration behind
        // it -- and so a refusal, which arrives as the stream failing rather than as a throw from
        // Join, surfaces from this call instead of from a later assertion about something else.
        if (!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
            || stream.ResponseStream.Current.CommandCase != Wire.NodeCommand.CommandOneofCase.Joined)
        {
            throw new InvalidOperationException(
                $"The first command on the join stream for '{machine}' was not an acknowledgement.");
        }

        return new JoinedNodeClient(nodes, machine, stream);
    }

    /// <summary>Counts every command that arrived within a window.</summary>
    /// <param name="within">How long to collect for.</param>
    /// <returns>How many arrived.</returns>
    /// <remarks>
    /// For asserting that N asks produce N commands rather than more. <c>WaitForCommandAsync</c>
    /// answers "did one arrive", which cannot tell one from six -- and one keystroke becoming six
    /// processes is exactly the failure worth checking on an operation that starts processes.
    /// </remarks>
    public async Task<int> CountCommandsAsync(TimeSpan within)
    {
        await Task.Delay(within).ConfigureAwait(false);

        lock (_commands)
        {
            return _commands.Count;
        }
    }

    /// <summary>Waits for a command to arrive.</summary>
    /// <param name="within">How long to wait before giving up.</param>
    /// <returns>The command.</returns>
    /// <exception cref="TimeoutException">None arrived.</exception>
    public async Task<Wire.NodeCommand> WaitForCommandAsync(TimeSpan within)
    {
        if (!await _arrived.WaitAsync(within).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"No command reached the node on '{Machine}' within {within}.");
        }

        lock (_commands)
        {
            return _commands[^1];
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var command in _stream.ResponseStream
                .ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                lock (_commands)
                {
                    _commands.Add(command);
                }

                _ = _arrived.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The ordinary end of a join.
        }
        catch (RpcException)
        {
            // The server went away, which for a test is a teardown rather than a fault.
        }
    }

    /// <summary>Ends the join, which is what tells the registry the machine has gone.</summary>
    /// <returns>A task that completes when the stream has closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the pump was cancelled above.
        }

        _stream.Dispose();
        _cancellation.Dispose();
        _arrived.Dispose();
    }
}

/// <summary>
/// A clock the tests move by hand.
/// </summary>
/// <remarks>
/// A stub rather than <c>FakeTimeProvider</c>, which would mean a package on the test path for two
/// members -- the same trade <c>Parley.Cli.Tests</c> and <c>Parley.Mcp.Tests</c> each made. It
/// exists here for the wake ceiling: two minutes is far too long to wait out, so without a movable
/// clock the case that matters -- a wake that never produced a drain -- is the one nothing could
/// reach.
/// </remarks>
/// <param name="now">The instant to start at.</param>
internal sealed class StubClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>Gets or sets the instant every read returns.</summary>
    public DateTimeOffset Now { get; set; } = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => Now += by;
}
