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
    /// <param name="clock">The clock to stamp from, or null for the system one.</param>
    /// <returns>The running broker.</returns>
    public static async Task<LoopbackBroker> StartAsync(
        CancellationToken cancellationToken,
        TimeProvider? clock = null)
    {
        // **Settable for the same reason the patience above is.** The wake ceiling is two minutes,
        // and a test that waited it out would be deleted by whoever next ran the suite -- so the
        // case that matters, a wake that never produced a drain, would be the one nothing could
        // reach. Defaults to the system clock, so every existing test is unaffected.
        clock ??= TimeProvider.System;

        var runtime = BrokerRuntime.OpenInMemory(BrokerOptions.Default, clock);

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
    /// <param name="owns">What the launch environment says this agent owns, or null for nothing.</param>
    /// <param name="adapter">
    /// The adapter it attaches through. <c>test</c> unless a test is attaching as the person, whose seat adapter is
    /// what a surface reports (specs/023).
    /// </param>
    /// <param name="launchToken">The token a node put in its environment, when a node started it (SPEC.md §11.7).</param>
    /// <param name="startedBy">
    /// The node that started it, which names the launch to the broker first and gives it the token — the way every agent
    /// a node runs attaches. Without one it is an agent somebody opened outside Parley, which no node gives a turn.
    /// </param>
    /// <returns>The attached client.</returns>
    public static async Task<AttachedClient> AttachAsync(
        LoopbackBroker broker,
        string name,
        string? workspace = null,
        CancellationToken cancellationToken = default,
        string? owns = null,
        string adapter = "test",
        string? launchToken = null,
        JoinedNodeClient? startedBy = null)
    {
        if (startedBy is not null)
        {
            launchToken = Guid.NewGuid().ToString("N");

            _ = await startedBy.Nodes.ReportLaunchAsync(
                new Wire.LaunchReport { Machine = startedBy.Machine, Agent = name, Token = launchToken },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var client = broker.Connect();

        var stream = client.Attach(
            new Wire.AttachRequest
            {
                Agent = name,
                Adapter = adapter,
                Tier = Wire.AgentTier.Pull,
                Workspace = workspace ?? string.Empty,

                // **The only way a line reaches the registry** (§12.3): an adapter says what its
                // launch environment carries, and no operator operation writes it. A test that needs
                // an agent whose peers already read a line has to attach as one, which is also the
                // honest shape -- there is no other route, and 020's whole point is that there is not.
                Owns = owns ?? string.Empty,

                // Announced, because the broker checks it. Taken from the constant rather than
                // written out, so a version bump does not need this file edited -- and so the
                // check cannot be satisfied by a literal that happens to match.
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,

                // What a node put in the process's environment, when a node started it (SPEC.md §11.7).
                LaunchToken = launchToken ?? string.Empty
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
    /// <param name="reportsTurns">Whether this node says it reports the turns it gives (SPEC.md §11.6).</param>
    /// <returns>The joined node.</returns>
    public static async Task<JoinedNodeClient> JoinAsync(
        LoopbackBroker broker,
        string machine,
        IEnumerable<string> startable,
        CancellationToken cancellationToken = default,
        Wire.NodeFacts? facts = null,
        bool reportsTurns = false)
    {
        var nodes = broker.ConnectAsNode();

        var request = new Wire.JoinRequest
        {
            Machine = machine,
            Facts = facts ?? new Wire.NodeFacts { Os = "test-os" },
            ReportsTurns = reportsTurns,
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

    /// <summary>Counts every command that arrived within a window, waiting for none of them.</summary>
    /// <param name="within">How long to collect for.</param>
    /// <returns>How many arrived.</returns>
    /// <remarks>
    /// <para>
    /// <b>Use this only where nothing is owed — an expectation of zero, or a ceiling.</b> It sleeps a
    /// fixed window and counts, so a command still in flight is indistinguishable from one never sent.
    /// Where a positive number is expected, that ambiguity is the whole assertion, and
    /// <see cref="CountCommandsAsync(int, TimeSpan, TimeSpan?)"/> is the one to call: it waits for what is
    /// owed first, then listens for what is not.
    /// </para>
    /// <para>
    /// <b>This is written as a rule because following it halfway is what happened.</b> The waiting overload
    /// was added with the argument beside it, and applied to exactly one of the fifteen call sites that
    /// needed it. The other fourteen went on sleeping, and surfaced as two different tests failing in two
    /// different full-suite runs — each passing every time its class ran alone, which is precisely the
    /// signature the other overload's remarks describe. One implementation of a rule and fourteen
    /// imitations of the thing it replaced.
    /// </para>
    /// <para>
    /// Counting at all is still the point where something is owed: <c>WaitForCommandAsync</c> answers "did
    /// one arrive", which cannot tell one from six — and one keystroke becoming six processes is exactly
    /// the failure worth checking on an operation that starts processes.
    /// </para>
    /// </remarks>
    public async Task<int> CountCommandsAsync(TimeSpan within)
    {
        await Task.Delay(within).ConfigureAwait(false);

        lock (_commands)
        {
            return _commands.Count;
        }
    }

    /// <summary>Waits for a number of commands to arrive, then counts what is there after a settling window.</summary>
    /// <param name="expected">How many are owed.</param>
    /// <param name="settle">How long to keep listening once they have, for ones that should not come.</param>
    /// <param name="within">How long to wait for the expected ones.</param>
    /// <returns>How many arrived in total.</returns>
    /// <remarks>
    /// <b>What <see cref="CountCommandsAsync(TimeSpan)"/> alone cannot do, and the flake it caused.</b> That waits a fixed
    /// window and counts, so under a loaded suite a command still in flight is indistinguishable from one never
    /// sent — and the assertion fails saying nothing was started when the real answer was "not yet". Seen as one
    /// failure in three full runs of this suite, passing every time the class ran alone.
    /// <para>
    /// So the two halves are separated: wait generously for the commands that are owed, then listen a little
    /// longer for ones that are not. The second half is what the count was always for, and it is the half a
    /// fixed sleep does correctly.
    /// </para>
    /// </remarks>
    public async Task<int> CountCommandsAsync(int expected, TimeSpan settle, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));

        while (DateTime.UtcNow < deadline)
        {
            lock (_commands)
            {
                if (_commands.Count >= expected)
                {
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
        }

        return await CountCommandsAsync(settle).ConfigureAwait(false);
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
