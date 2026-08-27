using Grpc.Core;
using Grpc.Net.Client;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Mcp;

/// <summary>
/// This adapter's connection to the broker of SPEC.md §4, held open for as long as it runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaced a class that opened the database.</b> That is the whole change, and it is
/// structural rather than stylistic: an adapter can no longer reach a store, because it no
/// longer references one. SQLite in WAL mode needs coherent shared memory between every process
/// touching the file, so the previous arrangement — every adapter opening it — worked only while
/// every adapter was on one machine, and failed as corruption rather than as an error the moment
/// one was not.
/// </para>
/// <para>
/// <b>The attach stream is the agent's presence.</b> §4.0: a connection is evidence. Opening it
/// is what registers this agent and releases whatever its last session abandoned; letting it end
/// is what says the agent has gone, and there is nothing to renew in between. The previous
/// arrangement wrote a timestamp and had every reader guess how stale was too stale.
/// </para>
/// <para>
/// <b>No gate, and none needed.</b> The old class serialised everything behind a semaphore
/// because it held a connection that was not thread-safe. It holds a socket now, and gRPC
/// multiplexes concurrent calls over one of those by design — the serialising is the broker's
/// problem, on the machine that owns the file.
/// </para>
/// <para>
/// <b>It re-attaches when the stream drops.</b> A broker restarts — an upgrade, a reboot, a
/// service manager restarting it — and every adapter's stream ends with it. Without this they
/// would stay running and refuse every operation as not attached until somebody noticed and
/// restarted each editor, which is the worst failure mode available: nothing crashes, nothing
/// logs, and a fleet of agents quietly stops answering.
/// </para>
/// </remarks>
public sealed class BrokerLink : IAsyncDisposable
{
    /// <summary>
    /// What this server records as the adapter attaching its agent.
    /// </summary>
    /// <remarks>
    /// A capability name rather than a vendor one, matching §11.1's classification. Every client
    /// that speaks MCP reaches the broker the same way, so the vendor of the one that launched
    /// this process tells an operator nothing they can act on.
    /// </remarks>
    public const string Adapter = "mcp";

    /// <summary>How long to wait before trying the broker again.</summary>
    /// <remarks>
    /// Fixed rather than backing off, and short. A broker being restarted is back within seconds
    /// and the cost of asking is one refused connection; the case a long backoff protects
    /// against — a thundering herd — does not arise here, because the herd is a handful of
    /// editors on one machine talking to a process on the same machine.
    /// </remarks>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    private readonly GrpcChannel _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Wire.AttachRequest _request;
    private readonly Task _pump;

    private AsyncServerStreamingCall<Wire.Event> _stream;
    private int _disposed;

    private BrokerLink(
        GrpcChannel channel,
        Wire.Agents.AgentsClient agents,
        AsyncServerStreamingCall<Wire.Event> stream,
        Wire.AttachRequest request,
        AgentName agent,
        Wire.Attached attached)
    {
        _channel = channel;
        _stream = stream;
        _request = request;

        Agents = agents;
        Agent = agent;
        Attached = attached;

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Gets the client every operation goes through.</summary>
    public Wire.Agents.AgentsClient Agents { get; }

    /// <summary>Gets the agent this server speaks for.</summary>
    public AgentName Agent { get; }

    /// <summary>Gets what the most recent attach sequence of SPEC.md §14 did.</summary>
    /// <value>
    /// Replaced when the link re-attaches after a broker restart, because the newer sequence is
    /// the one that describes what this agent currently owes.
    /// </value>
    public Wire.Attached Attached { get; private set; }

    /// <summary>Gets how much is waiting, as of the last notice the broker sent.</summary>
    /// <value>
    /// Updated whenever the broker speaks, and zero before it has. Read by nothing that decides
    /// protocol behaviour: it is a count for an adapter to surface, and the messages themselves
    /// arrive only through a drain, wrapped in the envelope §13 requires.
    /// </value>
    public Wire.Notice Waiting { get; private set; } = new();

    /// <summary>
    /// Connects to the broker and attaches this agent.
    /// </summary>
    /// <param name="options">What this server was launched with.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The link.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="AgentRetiredException">This agent's registration has been withdrawn.</exception>
    /// <exception cref="BrokerUnreachableException">Nothing is listening.</exception>
    /// <remarks>
    /// The attach runs at start-up rather than lazily, because §14 makes it the thing that
    /// invalidates what the previous session believed — and every operation refuses until it
    /// has. Doing it here means a launch either works or fails visibly, rather than producing a
    /// server whose every tool reports that the broker has not started.
    /// </remarks>
    public static async Task<BrokerLink> StartAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var channel = GrpcChannel.ForAddress(options.Broker, new GrpcChannelOptions
        {
            // A short connect timeout, because the default is ten seconds and this sits in front
            // of a model's turn. A broker that is down is down: waiting longer does not find it,
            // and every tool call blocking for ten seconds before reporting so turns one stopped
            // process into a session that feels broken.
            //
            // It bounds connecting only. A broker that answers and then takes its time is a
            // different problem, and one nothing here should be second-guessing -- an ask can
            // legitimately do real work.
            HttpHandler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),

                // Long enough that an idle attach stream is not torn down by a proxy or the
                // operating system, which would present as an agent that stops being told it has
                // mail after a quiet hour.
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            }
        });

        try
        {
            var agents = new Wire.Agents.AgentsClient(channel);

            var request = new Wire.AttachRequest
            {
                Agent = options.Agent.Value,
                Adapter = Adapter,
                Tier = Wire.AgentTier.Pull,
                Workspace = options.Workspace ?? string.Empty,
                Owns = options.Owns ?? string.Empty,
                ProtocolVersion = ProtocolVersion.Current
            };

            var stream = agents.Attach(request, cancellationToken: cancellationToken);

            // The first event is always the attach result. Awaited here so that a refusal — a
            // retired agent, a name held by another repository — arrives as a failed launch
            // rather than as a tool call failing later for reasons the model cannot act on.
            if (!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
                || stream.ResponseStream.Current.EventCase != Wire.Event.EventOneofCase.Attached)
            {
                throw new InvalidOperationException(
                    "The broker did not acknowledge the attach. This is a version mismatch " +
                    $"between this adapter and the broker at {options.Broker}.");
            }

            return new BrokerLink(
                channel, agents, stream, request, options.Agent,
                stream.ResponseStream.Current.Attached);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
        {
            channel.Dispose();

            throw new BrokerUnreachableException(options.Broker, failure);
        }
        catch (RpcException failure)
            when (failure.StatusCode == StatusCode.FailedPrecondition
                  && failure.Status.Detail.Contains("retired", StringComparison.OrdinalIgnoreCase))
        {
            channel.Dispose();

            throw new AgentRetiredException(options.Agent);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    private async Task PumpAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await foreach (var received in _stream.ResponseStream
                    .ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
                {
                    if (received.EventCase == Wire.Event.EventOneofCase.Notice)
                    {
                        Waiting = received.Notice;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The ordinary end of an attachment.
                return;
            }
            catch (RpcException)
            {
                // The broker went away, which is what the loop below is for.
            }

            if (!await ReattachAsync().ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Opens a new attach stream after the previous one ended.</summary>
    /// <returns><see langword="false"/> if this link is being disposed.</returns>
    /// <remarks>
    /// <para>
    /// The same request, deliberately. Re-attaching runs the sequence of SPEC.md §14 again —
    /// which releases whatever this agent abandoned when the broker went down, and is exactly
    /// what an agent coming back is supposed to do.
    /// </para>
    /// <para>
    /// A refusal that is not a connection failure ends the loop. A retired agent or a name
    /// another repository has taken will refuse identically for ever, and retrying it every two
    /// seconds would fill a log with the same line rather than surfacing anything.
    /// </para>
    /// </remarks>
    private async Task<bool> ReattachAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryAfter, _cancellation.Token).ConfigureAwait(false);

                var stream = Agents.Attach(_request, cancellationToken: _cancellation.Token);

                if (!await stream.ResponseStream.MoveNext(_cancellation.Token).ConfigureAwait(false)
                    || stream.ResponseStream.Current.EventCase
                        != Wire.Event.EventOneofCase.Attached)
                {
                    stream.Dispose();
                    continue;
                }

                var previous = Interlocked.Exchange(ref _stream, stream);
                previous.Dispose();

                Attached = stream.ResponseStream.Current.Attached;

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
            {
                // Still down. Round again.
            }
            catch (RpcException)
            {
                // Refused for a reason retrying cannot fix. See the remarks.
                return false;
            }
        }

        return false;
    }

    /// <summary>Ends the attachment, which is what tells the broker this agent has gone.</summary>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

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

        await _channel.ShutdownAsync().ConfigureAwait(false);
        _channel.Dispose();
    }
}
