using Grpc.Core;
using Parley.Broker;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Server;

/// <summary>
/// The operator surface of SPEC.md §10.2, served over the transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate service from <see cref="AgentsService"/>, and that is the point.</b> §10.2 opens
/// by warning that an adapter surfacing an operator operation into an agent's tool set would
/// breach §10.1 and, through it, §16. The document's own protection is a naming convention —
/// <c>force_conclude</c> is deliberately not <c>conclude</c> — which depends on whoever writes an
/// adapter reading carefully. Two services on the wire makes it structural: an adapter binds
/// <c>Agents</c>, and <c>Agents</c> has no method that retires anybody.
/// </para>
/// <para>
/// <b>It is not authenticated, and nothing here pretends otherwise.</b> §16 records a hostile
/// network as a non-goal, so anything that can reach the broker can reach this. The separation
/// is about what an <em>agent's adapter</em> can be tricked into exposing to a model, which is a
/// real and different problem: a model that can retire its peers can end a conversation it is
/// losing.
/// </para>
/// </remarks>
public sealed class OperatorsService : Wire.Operators.OperatorsBase
{
    private readonly BrokerRuntime _runtime;

    /// <summary>Creates the service over a runtime.</summary>
    /// <param name="runtime">The broker process's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public OperatorsService(BrokerRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> ForceConclude(
        Wire.ForceConcludeRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var changed = await Console(
            (console, now, ct) => console.ForceConcludeAsync(request.ConversationId, now, ct),
            context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> Retire(
        Wire.AgentRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var changed = await Console(
            (console, _, ct) => console.RetireAsync(agent, ct),
            context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> Reinstate(
        Wire.AgentRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var changed = await Console(
            (console, _, ct) => console.ReinstateAsync(agent, ct),
            context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.PurgeReply> Purge(
        Wire.AgentRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var outcome = await Console(
            (console, _, ct) => console.PurgeAsync(agent, ct),
            context).ConfigureAwait(false);

        var reply = new Wire.PurgeReply { Verdict = Conversions.Out(outcome.Verdict) };

        foreach (var toll in outcome.Destroyed)
        {
            reply.Destroyed.Add(new Wire.PurgeToll { Table = toll.Table, Rows = toll.Rows });
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.RegistryReply> DescribeRegistry(
        Wire.Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = await Console(
            (console, _, ct) => console.DescribeRegistryAsync(ct),
            context).ConfigureAwait(false);

        var reply = new Wire.RegistryReply();

        foreach (var row in rows)
        {
            reply.Rows.Add(Conversions.Out(row));
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.RefusalsReply> DescribeRefusals(
        Wire.Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var refusals = await Console(
            (console, _, ct) => console.DescribeRefusalsAsync(ct),
            context).ConfigureAwait(false);

        var reply = new Wire.RefusalsReply();

        foreach (var refusal in refusals)
        {
            reply.Refusals.Add(new Wire.SizeRefusal
            {
                Agent = refusal.Agent.Value,
                Type = Conversions.Out(refusal.Type),
                Refusals = refusal.Refusals,
                Worst = refusal.Worst,
                Total = refusal.Total,
                Cap = refusal.Cap,
                LastUtc = Conversions.Stamp(refusal.LastUtc)
            });
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ClarificationsReply> DescribeClarifications(
        Wire.Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await Console(
            (console, _, ct) => console.DescribeClarificationsAsync(ct),
            context).ConfigureAwait(false);

        var reply = new Wire.ClarificationsReply
        {
            ExchangesAtCap = report.ExchangesAtCap,
            LowestCapInForce = report.LowestCapInForce,
            HighestCapInForce = report.HighestCapInForce,
            Answers = report.Answers,
            AnswersResolvingSubQuestionsDifferently =
                report.AnswersResolvingSubQuestionsDifferently
        };

        foreach (var bucket in report.Distribution)
        {
            reply.Distribution.Add(new Wire.ClarifyDepthBucket
            {
                Depth = bucket.Depth,
                Exchanges = bucket.Exchanges
            });
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ReferralsReply> DescribeReferrals(
        Wire.Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await Console(
            (console, _, ct) => console.DescribeReferralsAsync(ct),
            context).ConfigureAwait(false);

        var reply = new Wire.ReferralsReply();

        foreach (var pair in report.Pairs)
        {
            reply.Pairs.Add(new Wire.ReferralPair
            {
                Asker = pair.Asker,
                Answerer = pair.Answerer,
                Closed = pair.Closed,
                OutOfScope = pair.OutOfScope
            });
        }

        foreach (var target in report.Targets)
        {
            reply.Targets.Add(new Wire.ReferralTarget
            {
                Asker = target.Asker,
                Answerer = target.Answerer,
                RedirectedTo = target.RedirectedTo,
                Referrals = target.Referrals,
                LastUtc = target.LastUtc
            });
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.NotificationsReply> DescribeNotifications(
        Wire.Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await Console(
            (console, _, ct) => console.DescribeNotificationsAsync(ct),
            context).ConfigureAwait(false);

        var reply = new Wire.NotificationsReply();

        foreach (var flow in report.Flows)
        {
            reply.Flows.Add(new Wire.NotifyFlow
            {
                From = flow.From,
                To = flow.To,
                Sent = flow.Sent,
                Characters = flow.Characters,
                Largest = flow.Largest,
                FirstUtc = flow.FirstUtc,
                LastUtc = flow.LastUtc
            });
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ExportReply> Export(
        Wire.ExportRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var messages = await Console(
            (console, _, ct) => console.ExportAsync(request.ConversationId, ct),
            context).ConfigureAwait(false);

        var reply = new Wire.ExportReply();

        foreach (var message in messages)
        {
            reply.Messages.Add(Conversions.Out(message));
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.PruneReply> Prune(
        Wire.Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await Console(
            (console, now, ct) => console.PruneAsync(_runtime.Retention, now, ct),
            context).ConfigureAwait(false);

        return new Wire.PruneReply
        {
            Removed = result.Removed,
            HeldByFloor = result.HeldByFloor,
            Contended = result.Contended,
            Capped = result.Capped
        };
    }

    /// <summary>Runs work against a console over the shared connection.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="context">The call.</param>
    /// <returns>Whatever the work returned.</returns>
    /// <remarks>
    /// A console is built per call rather than held, because it carries no state — unlike a
    /// broker, which is bound to the agent it attached for. What it needs is the connection, and
    /// the connection is what the gate protects.
    /// </remarks>
    private Task<T> Console<T>(
        Func<OperatorConsole, DateTimeOffset, CancellationToken, Task<T>> work,
        ServerCallContext context) =>
        _runtime.UseConnectionAsync(
            (connection, now, ct) => work(new OperatorConsole(connection), now, ct),
            context.CancellationToken);

    /// <summary>Reads an agent name, refusing anything that is not one.</summary>
    /// <param name="value">The name as it arrived.</param>
    /// <returns>The name.</returns>
    private static AgentName Name(string value) =>
        AgentName.TryCreate(value, out var name)
            ? name
            : throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"'{value}' is not an agent name. Names are role@machine (SPEC.md §4.1.1)."));
}
