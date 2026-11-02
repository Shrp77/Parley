using Grpc.Core;
using Grpc.Net.Client;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// A node that joins a broker and collects the commands it is sent.
/// </summary>
/// <remarks>
/// <para>
/// Smaller than <c>Parley.Server.Tests</c>' <c>JoinedNodeClient</c> and here rather than shared,
/// because that one lives in a project this cannot reference and the thing being tested —
/// <c>Registering</c> — is internal to <c>Parley.Setup</c>, whose grant reaches this assembly and
/// not that one.
/// </para>
/// <para>
/// It launches nothing. A start command is collected and answered by the test attaching an agent,
/// which is the honest division: what a real node does with a start is run a process, and what the
/// process does is attach — so a stub that attached by itself would be testing its own helpfulness.
/// </para>
/// </remarks>
internal sealed class NodeStub : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly AsyncServerStreamingCall<Wire.NodeCommand> _stream;
    private readonly CancellationTokenSource _cancellation = new();

    private NodeStub(GrpcChannel channel, AsyncServerStreamingCall<Wire.NodeCommand> stream)
    {
        _channel = channel;
        _stream = stream;
    }

    /// <summary>Joins a broker as this machine, announcing what it can start.</summary>
    /// <param name="address">The broker.</param>
    /// <param name="startable">The roles it says it can launch.</param>
    /// <param name="cancellationToken">Cancels the join.</param>
    /// <returns>The joined node.</returns>
    /// <exception cref="InvalidOperationException">The broker did not acknowledge.</exception>
    public static async Task<NodeStub> JoinAsync(
        string address, IEnumerable<string> startable, CancellationToken cancellationToken)
    {
        var channel = GrpcChannel.ForAddress(address);
        var nodes = new Wire.Nodes.NodesClient(channel);

        var request = new Wire.JoinRequest
        {
            Machine = MachineName.Current,
            Facts = new Wire.NodeFacts { Os = "test-os" }
        };

        request.StartableRoles.AddRange(startable);

        var stream = nodes.Join(request, cancellationToken: cancellationToken);

        try
        {
            // The acknowledgement is always first, per the contract. Awaited here so a test that joins
            // and immediately asks for a start is not racing the registration behind it.
            if (!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
                || stream.ResponseStream.Current.CommandCase
                    != Wire.NodeCommand.CommandOneofCase.Joined)
            {
                throw new InvalidOperationException("the broker did not acknowledge the join");
            }
        }
        catch
        {
            // A refused join is retried by some tests, so what it opened is closed here rather than left behind.
            stream.Dispose();
            channel.Dispose();
            throw;
        }

        return new NodeStub(channel, stream);
    }

    /// <summary>Waits for the next command after the acknowledgement.</summary>
    /// <param name="within">How long to allow.</param>
    /// <returns>The command.</returns>
    /// <exception cref="TimeoutException">None arrived.</exception>
    public async Task<Wire.NodeCommand> NextAsync(TimeSpan within)
    {
        using var giving = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);

        giving.CancelAfter(within);

        try
        {
            if (await _stream.ResponseStream.MoveNext(giving.Token).ConfigureAwait(false))
            {
                return _stream.ResponseStream.Current;
            }
        }
        catch (OperationCanceledException)
        {
        }

        throw new TimeoutException($"no command arrived within {within}");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        _stream.Dispose();
        _cancellation.Dispose();

        await _channel.ShutdownAsync().ConfigureAwait(false);

        _channel.Dispose();
    }
}

/// <summary>
/// An agent that attaches and holds its stream open.
/// </summary>
/// <remarks>
/// What a started process does, and the only thing in these tests that causes a registration —
/// deliberately, because §12.3 makes registering the adapter's own act and a test that wrote the
/// row itself would be exercising a path the deployment does not have.
/// </remarks>
internal sealed class AttachedStub : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly AsyncServerStreamingCall<Wire.Event> _stream;

    private AttachedStub(GrpcChannel channel, AsyncServerStreamingCall<Wire.Event> stream)
    {
        _channel = channel;
        _stream = stream;
    }

    /// <summary>Attaches as an agent and waits for the broker to acknowledge.</summary>
    /// <param name="address">The broker.</param>
    /// <param name="agent">Who to attach as.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The attached agent.</returns>
    /// <exception cref="InvalidOperationException">The broker did not acknowledge.</exception>
    public static async Task<AttachedStub> AttachAsync(
        string address, AgentName agent, CancellationToken cancellationToken)
    {
        var channel = GrpcChannel.ForAddress(address);
        var agents = new Wire.Agents.AgentsClient(channel);

        var stream = agents.Attach(
            new Wire.AttachRequest
            {
                Agent = agent.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,

                // Taken from the constant rather than written out, so a version bump does not need
                // this file edited.
                ProtocolVersion = Parley.Core.ProtocolVersion.Current
            },
            cancellationToken: cancellationToken);

        if (!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
            || stream.ResponseStream.Current.EventCase != Wire.Event.EventOneofCase.Attached)
        {
            throw new InvalidOperationException($"the broker did not attach {agent.Value}");
        }

        return new AttachedStub(channel, stream);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _stream.Dispose();

        await _channel.ShutdownAsync().ConfigureAwait(false);

        _channel.Dispose();
    }
}
