using System.Threading.Channels;
using Grpc.Core;
using Parley.Core;
using Parley.Store;
using Wire = Parley.Transport;

namespace Parley.Server;

/// <summary>
/// The machine surface of SPEC.md §4: one node per machine, holding one connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>A node is not a relay.</b> §4 says so and it shapes everything here: no message passes
/// through this service, and an agent's words never touch it. Its agents speak to the broker
/// through their own adapters under their own identities, and what a node contributes is the two
/// things only it can — whether the machine is up, and the ability to start a process on it.
/// </para>
/// <para>
/// <b>The connection is the machine being up.</b> Same argument as <c>Attach</c>, one level out.
/// The stream opening marks the node connected and the stream ending marks it gone, so a machine
/// that was switched off, lost its network, or had its supervisor killed all arrive as the same
/// event — and an agent registered there stops being startable at the moment there is genuinely
/// nothing left to start it.
/// </para>
/// <para>
/// <b>One node per machine, and a second is refused.</b> Two supervisors on one box would both
/// receive the same start command and race to launch the same agent, and the loser's process
/// would attach as a second session for an agent that only needed one. That is a
/// misconfiguration a person has to fix, so it is reported rather than tolerated — unlike an
/// agent, which legitimately has several sessions and counts them.
/// </para>
/// </remarks>
public sealed class NodesService : Wire.Nodes.NodesBase
{
    private readonly BrokerRuntime _runtime;

    /// <summary>Creates the service over a runtime.</summary>
    /// <param name="runtime">The broker process's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public NodesService(BrokerRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc/>
    public override async Task Join(
        Wire.JoinRequest request,
        IServerStreamWriter<Wire.NodeCommand> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var machine = Machine(request.Machine);

        var facts = request.Facts ?? new Wire.NodeFacts();

        var record = new NodeRecord(
            machine,
            Blank(facts.Os),
            Blank(facts.OsVersion),
            Blank(facts.IpAddress),
            Blank(facts.Domain),
            Connected: true,
            _runtime.Clock.GetUtcNow());

        JoinedNode joined;

        try
        {
            joined = await _runtime.JoinNodeAsync(
                record, request.StartableRoles, context.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(
                new Status(StatusCode.AlreadyExists, exception.Message, exception));
        }

        await using (joined.ConfigureAwait(false))
        {
            var here = await _runtime.UseConnectionAsync(
                (connection, _, ct) => new NodeRepository(connection).AgentsOnAsync(machine, ct),
                context.CancellationToken).ConfigureAwait(false);

            var acknowledgement = new Wire.Joined { Machine = machine };
            acknowledgement.Agents.AddRange(here.Select(a => a.Value));

            await responseStream.WriteAsync(
                new Wire.NodeCommand { Joined = acknowledgement }, context.CancellationToken)
                .ConfigureAwait(false);

            try
            {
                await foreach (var command in joined.Commands
                    .ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    await responseStream.WriteAsync(command, context.CancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The node went away, which is the ordinary end of a machine's participation.
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<Wire.DescribeNodeReply> Describe(
        Wire.DescribeNodeRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var machine = Machine(request.Machine);

        var (node, agents) = await _runtime.UseConnectionAsync(
            async (connection, _, ct) =>
            {
                var nodes = new NodeRepository(connection);

                return (
                    await nodes.GetAsync(machine, ct).ConfigureAwait(false),
                    await nodes.AgentsOnAsync(machine, ct).ConfigureAwait(false));
            },
            context.CancellationToken).ConfigureAwait(false);

        if (node is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"No machine called '{machine}' has ever been heard from. A machine appears here " +
                "when a node joins from it, or when an agent registers a name ending in it."));
        }

        var reply = new Wire.DescribeNodeReply
        {
            Machine = node.Machine,
            Connected = node.Connected,
            Facts = new Wire.NodeFacts
            {
                Os = node.Os ?? string.Empty,
                OsVersion = node.OsVersion ?? string.Empty,
                IpAddress = node.Address ?? string.Empty,
                Domain = node.Domain ?? string.Empty
            }
        };

        foreach (var agent in agents)
        {
            reply.Agents.Add(agent.Value);
        }

        return reply;
    }

    /// <summary>Reads a machine name, refusing anything that is not one.</summary>
    /// <param name="value">The name as it arrived.</param>
    /// <returns>The name.</returns>
    /// <remarks>
    /// Checked against the same grammar the machine half of an agent name uses, because they are
    /// the same thing: an agent on this machine is named <c>role@machine</c>, and a node whose
    /// name would not fit there could join and then be unable to host anybody.
    /// </remarks>
    private static string Machine(string value) =>
        AgentName.TryJoin("probe", value, out _)
            ? value
            : throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"'{value}' is not a machine name. It must be lower case, start with a letter " +
                "or digit, and contain only letters, digits, dots and hyphens (SPEC.md §4.1.1)."));

    /// <summary>Reads an optional string, treating the empty one as absent.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? Blank(string value) =>
        string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// One machine's open <c>Join</c> stream, which is the whole of what a node being up means.
/// </summary>
/// <remarks>
/// <b>The command queue is unbounded, unlike an agent's notice queue.</b> A notice is a count
/// that supersedes its predecessor, so keeping only the newest loses nothing. A start command
/// names a particular agent, so dropping one means an agent that a question is waiting on is
/// never launched — and nothing would report it, because the question sits there looking
/// ordinary until it is ghosted. The queue is instead bounded by the registry: a node can only
/// ever be told to start an agent registered to it.
/// </remarks>
public sealed class JoinedNode : IAsyncDisposable
{
    private readonly BrokerRuntime _runtime;
    private readonly Channel<Wire.NodeCommand> _commands =
        Channel.CreateUnbounded<Wire.NodeCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private int _closed;

    internal JoinedNode(BrokerRuntime runtime, string machine, IReadOnlySet<string> startable)
    {
        _runtime = runtime;
        Machine = machine;
        Startable = startable;
    }

    /// <summary>Gets the machine this stream belongs to.</summary>
    public string Machine { get; }

    /// <summary>Gets the roles this node said it can launch.</summary>
    /// <remarks>
    /// The node's own configuration, reported rather than stored. SPEC.md §4 keeps how to start
    /// a repository on the machine it lives on and never in the broker, so this is the broker
    /// knowing <em>whether</em> to send a command and never what the command would run.
    /// </remarks>
    public IReadOnlySet<string> Startable { get; }

    /// <summary>Gets the commands waiting to go down the stream.</summary>
    internal ChannelReader<Wire.NodeCommand> Commands => _commands.Reader;

    /// <summary>Asks the node to start an agent, if it said it could.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="because">Why, for the operator's benefit.</param>
    /// <returns><see langword="true"/> if the command was queued.</returns>
    internal bool RequestStart(AgentName agent, string because)
    {
        if (!Startable.Contains(agent.Role))
        {
            return false;
        }

        return _commands.Writer.TryWrite(new Wire.NodeCommand
        {
            Start = new Wire.StartAgent { Agent = agent.Value, Because = because }
        });
    }

    /// <summary>Ends the stream without touching the registry.</summary>
    internal void Close() => _ = _commands.Writer.TryComplete();

    /// <summary>Ends the stream and records that the machine has gone.</summary>
    /// <returns>A task that completes when the registry has been told.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        Close();

        await _runtime.ReleaseNodeAsync(this, CancellationToken.None).ConfigureAwait(false);
    }
}
