using System.Globalization;
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
    /// <summary>How many conversations a listing returns when the caller does not say.</summary>
    private const int DefaultPageSize = 50;

    /// <summary>The most a listing returns however many were asked for.</summary>
    /// <remarks>
    /// The broker's number rather than the caller's. §10.2 makes rule 8.5's argument one level
    /// up: an unbounded operator query is the mistake the message cap exists to prevent, and a
    /// bound the caller chooses is not a bound.
    /// </remarks>
    private const int MaximumPageSize = 200;

    /// <summary>How long an instruction waits for the agent to take its turn.</summary>
    /// <remarks>
    /// A turn is a model thinking, so this is generous and bounded rather than indefinite. The
    /// alternative is an operator holding a page open against a turn that may never end, and an
    /// instruction that outlives its patience settles as failed rather than staying pending.
    /// </remarks>
    private static readonly TimeSpan InstructionPatience = TimeSpan.FromMinutes(5);
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
    public override async Task<Wire.DidItReply> Archive(
        Wire.ConversationRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var changed = await Console(
            (console, now, ct) => console.SetConversationArchivedAsync(
                request.ConversationId, archived: true, now, ct),
            context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> Unarchive(
        Wire.ConversationRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var changed = await Console(
            (console, now, ct) => console.SetConversationArchivedAsync(
                request.ConversationId, archived: false, now, ct),
            context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DestroyReply> Destroy(
        Wire.ConversationRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var outcome = await Console(
            (console, now, ct) => console.DestroyConversationAsync(request.ConversationId, now, ct),
            context).ConfigureAwait(false);

        var reply = new Wire.DestroyReply { Verdict = Conversions.Out(outcome.Verdict) };

        foreach (var toll in outcome.Destroyed)
        {
            reply.Destroyed.Add(new Wire.PurgeToll { Table = toll.Table, Rows = toll.Rows });
        }

        return reply;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>This does not touch the registry, and that is the whole of why it is allowed to exist.</b>
    /// It asks the node whose catalogue names the role to launch it; the launched adapter then
    /// registers the one agent it attaches, by SPEC.md §12.3's ordinary route and under the identity
    /// §13 requires its launch to fix. Writing a registration here would be the forgery §12.3 forbids
    /// wearing an operator's clothes.
    /// </para>
    /// <para>
    /// It follows that an agent nothing has ever registered can be started, because what makes the
    /// role reachable is the node's catalogue rather than the registry — which is why enrolling a
    /// repository need not leave an agent nobody can reach until somebody opens that directory.
    /// </para>
    /// </remarks>
    public override Task<Wire.DidItReply> Start(
        Wire.StartRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var started = _runtime.RequestAttention(
            agent,
            string.IsNullOrWhiteSpace(request.Because) ? "an operator asked for it" : request.Because);

        return Task.FromResult(new Wire.DidItReply { Changed = started });
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

        var exported = await Console(
            (console, _, ct) => console.ExportAsync(request.ConversationId, ct),
            context).ConfigureAwait(false);

        var reply = new Wire.ExportReply();

        foreach (var message in exported.Messages)
        {
            reply.Messages.Add(Conversions.Out(message));
        }

        // Beside the messages rather than behind a second call. A transcript rendered without
        // them cannot tell an abandoned question from an answered one, and §10.2 says that is the
        // most misleading thing a console can do.
        foreach (var exchange in exported.Exchanges)
        {
            reply.Exchanges.Add(new Wire.ExchangeSnapshot
            {
                ExchangeId = exchange.ExchangeId,
                State = Conversions.Out(exchange.State),
                OpenedUtc = exchange.OpenedUtc.ToString("O", CultureInfo.InvariantCulture),
                ClosedUtc = exchange.ClosedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
            });
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.WaitingReply> DescribeWaiting(
        Wire.Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var edges = await Console(
            (console, _, ct) => console.DescribeWaitingAsync(ct),
            context).ConfigureAwait(false);

        var reply = new Wire.WaitingReply();

        foreach (var edge in edges)
        {
            reply.Edges.Add(new Wire.WaitingEdge
            {
                Waiter = edge.Waiter.Value,
                WaitedOn = edge.WaitedOn.Value,
                ExchangeId = edge.ExchangeId
            });
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.InstructReply> Instruct(
        Wire.InstructRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!AgentName.TryCreate(request.Agent, out var agent))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, $"'{request.Agent}' is not an agent name."));
        }

        // Said here rather than left to the schema. The instructions table carries the same cap
        // the messages table does, and reaching it as a constraint violation gives the operator a
        // failed write to read instead of a sentence about length — for the one refusal they can
        // actually do something about, since unlike an agent they can simply say less.
        if (request.Text.Length > MessageLimits.MaxPayloadCharacters)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"An instruction may be at most {MessageLimits.MaxPayloadCharacters} characters "
                + $"and this one is {request.Text.Length}."));
        }

        // Written before anybody tries to deliver it. The row records that a person asked, which
        // is worth having even when the asking failed — somebody wanting to know what they told
        // an agent last Tuesday is asking about the instruction, not about whether the machine
        // happened to be on.
        var id = await Console(
            (console, now, ct) => console.RecordInstructionAsync(agent, request.Text, now, ct),
            context).ConfigureAwait(false);

        var result = await _runtime.DeliverInstructionAsync(
            id, agent, request.Text, InstructionPatience, context.CancellationToken)
            .ConfigureAwait(false);

        // Settled here as well as by the reporting path, because an operator who gave up waiting
        // must not leave a row that says pending for ever.
        _ = await Console(
            (console, now, ct) => console.SettleInstructionAsync(
                id, result.Outcome, result.Reply, now, ct),
            context).ConfigureAwait(false);

        return new Wire.InstructReply
        {
            Outcome = Conversions.Out(result.Outcome),
            Reply = result.Reply ?? string.Empty
        };
    }

    /// <inheritdoc/>
    public override Task<Wire.ImplementsReply> Implements(
        Wire.Empty request, ServerCallContext context) =>
        // Asked before anything else, so a client can tell a broker of the wrong version from one
        // that is switched off. Those want different words in front of a person.
        Task.FromResult(new Wire.ImplementsReply { ProtocolVersion = ProtocolVersion.Current });

    /// <inheritdoc/>
    public override async Task<Wire.ListConversationsReply> ListConversations(
        Wire.ListConversationsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // The broker bounds the page whatever was asked for. §10.2 makes rule 8.5's argument one
        // level up: an unbounded operator query is the mistake the message cap prevents, and a
        // caller's number is a caller's number.
        var limit = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaximumPageSize);

        AgentName? participant = null;

        if (!string.IsNullOrEmpty(request.Participant))
        {
            // Refused rather than ignored. Dropping a filter that could not be parsed answers a
            // narrow question with a wide result, and the caller has no way to tell: an empty
            // list invites a second look, a full one reads as the answer. The console says so on
            // its own page, but the console is not the only thing that may ask.
            if (!AgentName.TryCreate(request.Participant, out var named))
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"'{request.Participant}' is not an agent name, so it cannot narrow this "
                    + "list. Ask again without it to see everything."));
            }

            participant = named;
        }

        // The token carries the instant and the identifier at it, separated by a space -- an
        // instant alone cannot resume, because the order breaks ties on the identifier and a
        // resume that ignored it would skip every remaining conversation sharing one instant.
        // Opaque to the caller either way: §10.2 says the broker bounds the page, and a token
        // whose shape a client parsed would be a shape the broker could not change.
        var resumeAt = request.PageToken.Split(' ', 2);

        var summaries = await Console(
            (console, _, ct) => console.ListConversationsAsync(
                participant,
                Parse(request.SinceUtc),
                Parse(request.UntilUtc),
                limit,
                Parse(resumeAt[0]),
                resumeAt.Length > 1 ? resumeAt[1] : null,
                request.IncludeArchived,
                ct),
            context).ConfigureAwait(false);

        // Counted whether or not archived ones were asked for, because the question it answers is
        // "are there archived conversations here" and a caller that could not ask would be hiding
        // traffic: an archived conversation goes on receiving messages.
        var omitted = request.IncludeArchived
            ? 0
            : await Console(
                (console, _, ct) => console.CountArchivedConversationsAsync(
                    participant, Parse(request.SinceUtc), Parse(request.UntilUtc), ct),
                context).ConfigureAwait(false);

        var reply = new Wire.ListConversationsReply { ArchivedOmitted = omitted };

        foreach (var summary in summaries)
        {
            var row = new Wire.ConversationSummary
            {
                ConversationId = summary.Id,
                Messages = summary.Messages,
                FirstActivityUtc = summary.FirstActivityUtc.ToString("O", CultureInfo.InvariantCulture),
                LastActivityUtc = summary.LastActivityUtc.ToString("O", CultureInfo.InvariantCulture),
                HasOpenExchange = summary.HasOpenExchange,
                Archived = summary.Archived
            };

            foreach (var agent in summary.Participants)
            {
                row.Participants.Add(agent.Value);
            }

            reply.Conversations.Add(row);
        }

        // The resume point is the last row on this page, which is a value that does not move. An
        // offset would re-read rows that shift as the log grows, showing one conversation twice
        // and another never. Both halves, for the reason above: the instant alone does not
        // identify a position when several conversations share it.
        reply.NextPageToken = summaries.Count < limit
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{summaries[^1].LastActivityUtc:O} {summaries[^1].Id}");

        return reply;
    }

    /// <summary>Reads an optional instant from the wire, where empty means absent.</summary>
    /// <param name="value">The rendered instant, or empty.</param>
    /// <returns>The instant, or <see langword="null"/>.</returns>
    private static DateTimeOffset? Parse(string value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

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
