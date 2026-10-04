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
    /// <inheritdoc/>
    public override async Task<Wire.Empty> ReportInstruction(
        Wire.InstructionReport request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // **Fitted to the cap before it is stored, and the schema refuses anything larger.** This
        // was unbounded: the instruction's own text has carried rule 8.4's cap for two migrations
        // and the agent's answer carried nothing, so anything able to reach the broker and claim to
        // be a node — which §16 says is anything on the network — could write an arbitrarily large
        // blob into the operator's record. Found by the review gate with a 200,000-character reply
        // that was stored and read back whole.
        //
        // Truncated rather than refused. Refusing would leave the instruction unsettled and lose
        // the answer, which is the failure the readback exists to remove; a shortened answer is
        // worth more than none, and the marker is what stops a reader taking the cut for the
        // agent's own ending.
        var result = new InstructionResult(
            Conversions.In(request.Outcome),
            string.IsNullOrEmpty(request.Reply) ? null : MessageLimits.Fit(request.Reply));

        // Settled from here as well as from the operator's call, because the two race and either
        // may arrive first. The row must not be left saying pending because whoever asked gave up
        // waiting before the agent finished thinking.
        _ = await _runtime.UseConnectionAsync(
            (connection, now, ct) => new Parley.Store.InstructionRepository(connection)
                .SettleAsync(request.InstructionId, result.Outcome, result.Reply, now, ct),
            context.CancellationToken).ConfigureAwait(false);

        _ = _runtime.CompleteInstruction(request.InstructionId, result);

        return new Wire.Empty();
    }

    /// <inheritdoc/>
    public override Task<Wire.Empty> ReportLaunch(Wire.LaunchReport request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Ignored rather than refused when it does not check out, as a turn report is: a node never stops a launch over a
        // report, so a refusal would tell it nothing it could act on (SPEC.md §11.7).
        if (AgentName.TryCreate(request.Agent, out var agent))
        {
            _ = _runtime.LaunchReported(Machine(request.Machine), agent, request.Token);
        }

        return Task.FromResult(new Wire.Empty());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Ignored rather than refused when it does not check out</b> (SPEC.md §11.6): an agent on another machine, a node
    /// not joined, an agent this broker does not know, a time that does not parse. A node never fails a turn over a
    /// report, so a refusal would tell it nothing it could act on — and nothing is stored either way.
    /// </remarks>
    public override async Task<Wire.Empty> ReportTurn(Wire.TurnReport request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!AgentName.TryCreate(request.Agent, out var agent))
        {
            return new Wire.Empty();
        }

        var machine = Machine(request.Machine);

        switch (request.PhaseCase)
        {
            case Wire.TurnReport.PhaseOneofCase.Started
                when Timestamp(request.Started.StartedUtc) is { } started:
                _ = await _runtime.TurnStartedAsync(machine, agent, started, context.CancellationToken)
                    .ConfigureAwait(false);
                break;

            case Wire.TurnReport.PhaseOneofCase.Ended
                when Timestamp(request.Ended.StartedUtc) is { } began
                    && request.Ended.ElapsedMs is >= 0 and <= LongestTurnMs:
                var ended = request.Ended;

                _ = await _runtime.TurnEndedAsync(
                    machine,
                    agent,
                    new EndedTurn(
                        began,
                        TimeSpan.FromMilliseconds(ended.ElapsedMs),
                        ended.Clean,
                        ended.TimedOut,
                        ended.ProcessDied,
                        Math.Max(0, ended.PermissionDenials),
                        double.IsFinite(ended.CostUsd) ? Math.Max(0, ended.CostUsd) : 0,
                        Math.Max(0, ended.ModelTurns)),
                    context.CancellationToken).ConfigureAwait(false);
                break;
        }

        return new Wire.Empty();
    }

    /// <summary>The longest turn a report may claim: a day.</summary>
    /// <remarks>
    /// A node gives up on a turn after twenty minutes, so a day is not a limit any real turn meets. What it stops is a
    /// length that put a turn's end past the calendar, which made every later registry and flags read throw — one report
    /// breaking both for the whole broker (the review gate's finding). A report beyond it is ignored like any other that
    /// does not check out.
    /// </remarks>
    internal const long LongestTurnMs = 24L * 60 * 60 * 1000;

    /// <summary>Reads a time from the wire, or null when it is not one.</summary>
    /// <param name="value">RFC 3339, in UTC.</param>
    /// <returns>The instant.</returns>
    private static DateTimeOffset? Timestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

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
                record, request.StartableRoles, context.CancellationToken, request.ReportsTurns).ConfigureAwait(false);
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
/// names a particular agent, so dropping one means an agent that a Request is waiting on is
/// never launched — and nothing would say why, because the Request sits there looking ordinary,
/// flagged only as not collected. The queue is instead bounded by the registry: a node can only
/// ever be told to start an agent registered to it.
/// </remarks>
public sealed class JoinedNode : IAsyncDisposable
{
    private readonly BrokerRuntime _runtime;
    private readonly Channel<Wire.NodeCommand> _commands =
        Channel.CreateUnbounded<Wire.NodeCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private int _closed;

    internal JoinedNode(BrokerRuntime runtime, string machine, IReadOnlySet<string> startable, bool reportsTurns = false)
    {
        _runtime = runtime;
        Machine = machine;
        Startable = startable;
        ReportsTurns = reportsTurns;
    }

    /// <summary>Whether this node said it reports the turns it gives (SPEC.md §11.6).</summary>
    /// <remarks>
    /// An agent on a node that did not say so is unknown rather than stopped: an older node reports nothing, and nothing
    /// must not read as stopped.
    /// </remarks>
    public bool ReportsTurns { get; }

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

    /// <summary>Asks the node to deliver an operator's instruction to an agent it holds.</summary>
    /// <param name="instructionId">The instruction, so its report can be matched to this asking.</param>
    /// <param name="agent">Who it is for.</param>
    /// <param name="text">What the operator said.</param>
    /// <returns><see langword="true"/> if the command was queued.</returns>
    /// <remarks>
    /// Queuing is not delivering. The node answers with <c>ReportInstruction</c> when the agent
    /// has had its turn, which is why the instruction carries an identifier: the command stream
    /// runs one way, so nothing about this call can carry the outcome back.
    /// </remarks>
    internal bool RequestInstruction(long instructionId, AgentName agent, string text)
    {
        if (!Startable.Contains(agent.Role))
        {
            return false;
        }

        return _commands.Writer.TryWrite(new Wire.NodeCommand
        {
            Instruct = new Wire.DeliverInstruction
            {
                InstructionId = instructionId,
                Agent = agent.Value,
                Text = text
            }
        });
    }

    /// <summary>Asks the node to give an agent it already holds a turn.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="because">Why, for the operator's benefit.</param>
    /// <returns><see langword="true"/> if the command was queued.</returns>
    /// <remarks>
    /// Sent when an agent is attached, which is exactly when a start is not asked for. The node
    /// gives the turn only if the session is one of its own residents, because an agent attached
    /// through somebody's editor window is not something this machine can drive.
    /// </remarks>
    internal bool RequestTurn(AgentName agent, string because)
    {
        if (!Startable.Contains(agent.Role))
        {
            return false;
        }

        return _commands.Writer.TryWrite(new Wire.NodeCommand
        {
            Turn = new Wire.GiveTurn { Agent = agent.Value, Because = because }
        });
    }

    /// <summary>Asks the node to restart an agent it started, once its turn is over (SPEC.md §11.1).</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="because">Why, for the operator's benefit.</param>
    /// <returns><see langword="true"/> if the command was queued.</returns>
    /// <remarks>
    /// <para>
    /// <b>The node decides when, because only the node knows whether a turn is in progress</b> (research finding 7).
    /// The broker has no turn state and must not grow one; it sends this when the agent owes nothing, and the node
    /// waits for the turn lock before stopping anything.
    /// </para>
    /// <para>
    /// Only for a role the node said it can launch, as a start is: a node cannot restart what it could not have
    /// started, and an agent attached from somebody's editor window is refreshed by nothing.
    /// </para>
    /// </remarks>
    internal bool RequestRefresh(AgentName agent, string because)
    {
        if (!Startable.Contains(agent.Role))
        {
            return false;
        }

        return _commands.Writer.TryWrite(new Wire.NodeCommand
        {
            Refresh = new Wire.RefreshAgent { Agent = agent.Value, Because = because }
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
