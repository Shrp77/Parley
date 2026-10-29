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
/// <b>A separate service from <see cref="AgentsService"/>, and that is the point.</b> An adapter binds
/// <c>Agents</c>, and <c>Agents</c> has no method that retires anybody or closes a conversation over its
/// participants.
/// </para>
/// <para>
/// <b>It is not authenticated, and nothing here pretends otherwise.</b> §16 records a hostile network as a
/// non-goal. The separation is about what an agent's adapter can be tricked into exposing to a model.
/// </para>
/// </remarks>
public sealed class OperatorsService : Wire.Operators.OperatorsBase
{
    private const int DefaultPageSize = 50;

    /// <summary>The most a listing returns however many were asked for.</summary>
    /// <remarks>The broker's number rather than the caller's: a bound the caller chooses is not a bound.</remarks>
    private const int MaximumPageSize = 200;

    private readonly BrokerRuntime _runtime;

    /// <summary>Creates the service over a runtime.</summary>
    /// <param name="runtime">The broker process's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public OperatorsService(BrokerRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>Refuses a conversation identifier that is blank, before anything opens the store.</summary>
    /// <param name="conversationId">What the caller sent.</param>
    /// <returns>The identifier, when there is one.</returns>
    /// <exception cref="RpcException">It is blank.</exception>
    /// <remarks>
    /// <para>
    /// <b>The guard lives here, not in a client.</b> A blank identifier reaching the repository
    /// throws, and a broker answering `Unknown: Exception was thrown by handler` says nothing an
    /// operator can act on and reads like a fault in the deployment. A rule enforced by whichever
    /// client happens to have it leaves with that client.
    /// </para>
    /// <para>
    /// Blank only, deliberately. The schema constrains the character set and refuses to store an
    /// identifier outside it, so one this rejected but storage would have accepted is a refusal the
    /// operator cannot act on; and one storage would have rejected can only ever match nothing,
    /// which the "no messages in '...'" answer already says clearly.
    /// </para>
    /// </remarks>
    private static string Identified(string conversationId) =>
        string.IsNullOrWhiteSpace(conversationId)
            ? throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "A conversation identifier is required, and blank is not one."))
            : conversationId;

    /// <inheritdoc/>
    /// <remarks>
    /// Participants still attached are nudged afterwards, so a receiver working in a live session learns the
    /// work was ended without waiting for its next collection. Nobody is started for it: a closed Result is
    /// news about work that no longer needs doing, and buying a turn to deliver it would spend the person's
    /// money on the thing they just stopped.
    /// </remarks>
    public override async Task<Wire.DidItReply> ForceConclude(Wire.ForceConcludeRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason;

        var changed = await Console(
            (console, now, ct) => console.ForceConcludeAsync(Identified(request.ConversationId), reason, now, ct),
            context).ConfigureAwait(false);

        if (changed)
        {
            var exported = await Console(
                (console, _, ct) => console.ExportAsync(Identified(request.ConversationId), ct), context).ConfigureAwait(false);

            foreach (var told in exported.Messages
                         .Where(m => m.ResponderModel == Responder.Broker)
                         .Select(m => m.To)
                         .Distinct())
            {
                await _runtime.NudgeAsync(told, context.CancellationToken).ConfigureAwait(false);
            }
        }

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> Archive(Wire.ConversationRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var changed = await Console(
            (console, now, ct) => console.SetConversationArchivedAsync(Identified(request.ConversationId), archived: true, now, ct),
            context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> Unarchive(Wire.ConversationRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var changed = await Console(
            (console, now, ct) => console.SetConversationArchivedAsync(Identified(request.ConversationId), archived: false, now, ct),
            context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DestroyReply> Destroy(Wire.ConversationRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // **Asked before anything is held**, unlike its siblings, which can ask inside the work
        // because the work is all they do. This one takes a hold over every file in the deployment
        // first, so a blank identifier reaching the guard further down would stop every download in
        // progress on its way to being refused.
        var conversationId = Identified(request.ConversationId);

        // Its files go with it (SPEC.md §14), once every download in progress has finished: the rows first, in the
        // destroy's own transaction, then the folder on disk — and only when the rows really went.
        var files = new FileWork(_runtime);

        using var held = await files.HoldEverythingAsync(context.CancellationToken).ConfigureAwait(false);

        var folder = await _runtime.UseConnectionAsync(
            (connection, _, ct) => new Parley.Store.SharedFileRepository(connection)
                .GetFolderAsync(conversationId, cancellationToken: ct),
            context.CancellationToken).ConfigureAwait(false);

        var outcome = await Console(
            (console, now, ct) => console.DestroyConversationAsync(conversationId, now, ct),
            context).ConfigureAwait(false);

        await DeleteFoldersWhoseRowsWentAsync(files, folder is null ? [] : [folder]).ConfigureAwait(false);

        var reply = new Wire.DestroyReply { Verdict = Conversions.Out(outcome.Verdict) };
        reply.Destroyed.AddRange(outcome.Destroyed.Select(t => new Wire.PurgeToll { Table = t.Table, Rows = t.Rows }));

        return reply;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>This does not touch the registry.</b> It asks the node whose catalogue names the role to launch it;
    /// the launched adapter registers the one agent it attaches, by §12.3's ordinary route.
    /// </para>
    /// <para>
    /// A retired agent is not started: retirement is a person's decision that nothing is routed to it, and
    /// <c>reinstate</c> is the route that undoes it. An agent the registry does not hold is started, because
    /// nothing about it has been retired.
    /// </para>
    /// </remarks>
    public override async Task<Wire.DidItReply> Start(Wire.StartRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Acted(request.Agent);

        // A second process in one checkout is two models editing it (SPEC.md §11.7).
        if (_runtime.OpenOutside(agent))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"'{agent.Value}' is already open outside Parley — somebody started it in an editor — so starting another " +
                "would put two models in one checkout. Close that session first, or prompt it there."));
        }

        var registry = await Console((console, _, ct) => console.DescribeRegistryAsync(ct), context)
            .ConfigureAwait(false);

        if (registry.FirstOrDefault(row => row.Name == agent) is { Status: AgentStatus.Retired })
        {
            return new Wire.DidItReply { Changed = false };
        }

        var started = _runtime.RequestAttention(
            agent, string.IsNullOrWhiteSpace(request.Because) ? "the person asked for it" : request.Because);

        return new Wire.DidItReply { Changed = started };
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> Retire(Wire.AgentRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Acted(request.Agent);
        var changed = await Console((console, _, ct) => console.RetireAsync(agent, ct), context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.RecordOwnershipReply> RecordOwnership(
        Wire.RecordOwnershipRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Subject(request.Agent);

        // Zero and empty are how proto3 spells "not set", and both mean something here: a composed
        // line has no proposal to have a length and no conversation to name. Mapping them to null
        // rather than to 0 and "" is what keeps `proposed_length` a fact about an agent's message
        // rather than a number somebody's serialiser chose.
        var acceptance = new OwnershipAcceptance(
            request.Statement,
            Conversions.In(request.Source),
            request.ProposedLength > 0 ? request.ProposedLength : null,
            string.IsNullOrEmpty(request.ConversationId) ? null : request.ConversationId);

        var record = await Console(
            (console, now, ct) => console.RecordOwnershipAsync(agent, acceptance, now, ct),
            context).ConfigureAwait(false);

        // A person's line answers whatever Parley was still waiting on for this agent, so the question settles and its
        // conversation closes. Left alone it would stay waiting beside a line that already answers it, and the agent
        // could never be asked again.
        if (record.Recorded)
        {
            _ = await _runtime.UseFacilitatorAsync(
                (facilitator, parley, now, ct) => facilitator.SettledByPersonAsync(parley, agent, now, cancellationToken: ct),
                context.CancellationToken).ConfigureAwait(false);
        }

        return new Wire.RecordOwnershipReply
        {
            Recorded = record.Recorded,
            Problem = record.Problem ?? string.Empty,
            LiveStatement = record.LiveStatement ?? string.Empty,
            Pending = record.Pending
        };
    }

    /// <inheritdoc/>
    public override async Task<Wire.FacilitationReply> Facilitation(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var report = await _runtime.UseFacilitatorAsync(
            (facilitator, _, _, ct) => facilitator.ReportAsync(ct), context.CancellationToken).ConfigureAwait(false);

        var reply = new Wire.FacilitationReply { AutoAccept = report.AutoAccept };
        reply.Questions.AddRange(report.Questions.Select(Conversions.Out));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.SetAutoAcceptReply> SetAutoAccept(
        Wire.SetAutoAcceptRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var on = await _runtime.UseFacilitatorAsync(
            (facilitator, _, now, ct) => facilitator.SetAutoAcceptAsync(request.On, now, ct),
            context.CancellationToken).ConfigureAwait(false);

        return new Wire.SetAutoAcceptReply { On = on };
    }

    /// <inheritdoc/>
    public override async Task<Wire.FacilitationActionReply> AskAgain(Wire.AgentRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var outcome = await _runtime.UseFacilitatorAsync(
            (facilitator, parley, now, ct) => facilitator.AskAgainAsync(parley, agent, now, ct),
            context.CancellationToken).ConfigureAwait(false);

        // Woken exactly as any Request's recipient is, through the same bounded path.
        if (outcome.Done)
        {
            await _runtime.NudgeAsync(agent, context.CancellationToken).ConfigureAwait(false);
            _ = _runtime.RequestAttentionForMail(agent, "a person asked Parley to ask it what it owns");
        }

        return Out(outcome);
    }

    /// <inheritdoc/>
    public override async Task<Wire.FacilitationActionReply> ReplyToAskedBack(
        Wire.ReplyToAskedBackRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var outcome = await _runtime.UseFacilitatorAsync(
            (facilitator, parley, now, ct) => facilitator.ReplyAsync(parley, agent, request.Text, now, ct),
            context.CancellationToken).ConfigureAwait(false);

        if (outcome.Done)
        {
            await _runtime.NudgeAsync(agent, context.CancellationToken).ConfigureAwait(false);
            _ = _runtime.RequestAttentionForMail(agent, "a person replied to what it asked Parley");
        }

        return Out(outcome);
    }

    /// <inheritdoc/>
    public override async Task<Wire.FacilitationActionReply> DiscardAnswer(Wire.AgentRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        return Out(await _runtime.UseFacilitatorAsync(
            (facilitator, parley, now, ct) => facilitator.DiscardAsync(parley, agent, now, ct),
            context.CancellationToken).ConfigureAwait(false));
    }

    private static Wire.FacilitationActionReply Out(FacilitationAction outcome) =>
        new() { Done = outcome.Done, Problem = outcome.Problem ?? string.Empty };

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> Reinstate(Wire.AgentRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Acted(request.Agent);
        var changed = await Console((console, _, ct) => console.ReinstateAsync(agent, ct), context).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = changed };
    }

    /// <inheritdoc/>
    public override async Task<Wire.PurgeReply> Purge(Wire.AgentRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Acted(request.Agent);

        // Every conversation it took part in goes, and placing a file made it a participant, so its files go too
        // (SPEC.md §14). The folders are read before the purge, because afterwards nothing names them.
        var files = new FileWork(_runtime);

        using var held = await files.HoldEverythingAsync(context.CancellationToken).ConfigureAwait(false);

        // **Every recorded folder, before and after**, rather than the ones the purge would delete, read before it: a
        // Request joining the agent to a conversation that has files can land between the read and the purge, and that
        // conversation's folder was then left on disk with no row naming it, until whatever next swept the location.
        var before = await RecordedFoldersAsync(context.CancellationToken).ConfigureAwait(false);

        var outcome = await Console((console, _, ct) => console.PurgeAsync(agent, ct), context).ConfigureAwait(false);

        // Not cancelled from here: the rows have gone, so their folders go whatever the caller does.
        var after = await RecordedFoldersAsync(CancellationToken.None).ConfigureAwait(false);
        var folders = before.Except(after, StringComparer.Ordinal).ToList();

        await DeleteFoldersWhoseRowsWentAsync(files, folders).ConfigureAwait(false);

        var reply = new Wire.PurgeReply { Verdict = Conversions.Out(outcome.Verdict) };
        reply.Destroyed.AddRange(outcome.Destroyed.Select(t => new Wire.PurgeToll { Table = t.Table, Rows = t.Rows }));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.RegistryReply> DescribeRegistry(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = await Console((console, _, ct) => console.DescribeRegistryAsync(ct), context).ConfigureAwait(false);

        // **Each agent's turn standing, from what its node reported** (SPEC.md §10.2, §11.6): whether a turn is
        // running from the broker's memory, how the last one ended from `invocations`. On this read only — the agent
        // roster carries none of it.
        var latest = (await _runtime.UseConnectionAsync(
                (connection, _, ct) => new Parley.Store.InvocationRepository(connection).LatestTurnsAsync(ct),
                context.CancellationToken).ConfigureAwait(false))
            .ToDictionary(turn => turn.Agent);

        var reply = new Wire.RegistryReply();
        reply.Rows.AddRange(rows.Select(row => Conversions.Out(row with
        {
            Turns = new TurnStanding(
                _runtime.TurnsReported(row.Name),
                _runtime.TurnRunningSince(row.Name),
                latest.TryGetValue(row.Name, out var last) ? last.EndedUtc : null,
                last?.Clean ?? false,
                last?.PermissionDenials ?? 0),
            OpenOutside = _runtime.OpenOutside(row.Name),
        })));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.RefusalsReply> DescribeRefusals(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var refusals = await Console((console, _, ct) => console.DescribeRefusalsAsync(ct), context)
            .ConfigureAwait(false);

        var reply = new Wire.RefusalsReply();
        reply.Refusals.AddRange(refusals.Select(r => new Wire.SizeRefusal
        {
            Agent = r.Agent.Value,
            Type = Conversions.Out(r.Type),
            Refusals = r.Refusals,
            Worst = r.Worst,
            Total = r.Total,
            Cap = r.Cap,
            LastUtc = Conversions.Stamp(r.LastUtc)
        }));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ClarificationsReply> DescribeClarifications(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await Console((console, _, ct) => console.DescribeClarificationsAsync(ct), context)
            .ConfigureAwait(false);

        var reply = new Wire.ClarificationsReply
        {
            ExchangesAtCap = report.ExchangesAtCap,
            LowestCapInForce = report.LowestCapInForce,
            HighestCapInForce = report.HighestCapInForce,
            Results = report.Results,
            ResultsWithMixedOutcomes = report.ResultsWithMixedOutcomes
        };

        reply.Distribution.AddRange(report.Distribution.Select(b => new Wire.ClarifyDepthBucket
        {
            Depth = b.Depth,
            Exchanges = b.Exchanges
        }));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ResultsReply> DescribeResults(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await Console((console, _, ct) => console.DescribeResultsAsync(ct), context).ConfigureAwait(false);

        var reply = new Wire.ResultsReply();

        reply.Outcomes.AddRange(report.Outcomes.Select(o => new Wire.OutcomeCount
        {
            Agent = o.Agent,
            ResponderModel = o.ResponderModel,
            Outcome = Conversions.Out(o.Outcome),
            Items = o.Items
        }));

        reply.Blockers.AddRange(report.Blockers.Select(b => new Wire.BlockerCount
        {
            Agent = b.Agent,
            Kind = Conversions.Out(b.Kind),
            Items = b.Items
        }));

        reply.Sources.AddRange(report.Sources.Select(s => new Wire.SourceCount
        {
            Agent = s.Agent,
            Source = s.Source,
            Items = s.Items
        }));

        reply.Closures.AddRange(report.Closures.Select(c => new Wire.ClosureCount
        {
            Agent = c.Agent,
            ByConsensus = c.ByConsensus,
            ByPerson = c.ByPerson
        }));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.FlagsReply> DescribeFlags(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await Console((console, now, ct) => console.DescribeFlagsAsync(now, ct), context)
            .ConfigureAwait(false);

        var reply = new Wire.FlagsReply();

        reply.Flags.AddRange(report.Exchanges.Select(f => new Wire.FlagView
        {
            Kind = Conversions.Out(f.Kind),
            ExchangeId = f.ExchangeId,
            ConversationId = f.ConversationId,
            WaitingOn = f.WaitingOn.Value,
            Waiting = f.Waiting.Value,
            SinceUtc = Conversions.Stamp(f.SinceUtc)
        }));

        foreach (var cycle in report.Cycles)
        {
            var view = new Wire.CycleView
            {
                ExchangeId = cycle.ExchangeId,
                ConversationId = cycle.ConversationId,
                RecordedUtc = Conversions.Stamp(cycle.RecordedUtc)
            };

            view.Path.AddRange(cycle.Path.Select(a => a.Value));
            reply.Cycles.Add(view);
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ExportReply> Export(Wire.ExportRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var exported = await Console((console, _, ct) => console.ExportAsync(Identified(request.ConversationId), ct), context)
            .ConfigureAwait(false);

        var reply = new Wire.ExportReply();

        if (exported.Destroyed is { } gone)
        {
            var record = new Wire.DestroyedConversation
            {
                ConversationId = gone.Id,
                DestroyedUtc = gone.DestroyedUtc.ToString("O", CultureInfo.InvariantCulture),
                Messages = gone.Messages
            };

            record.Participants.AddRange(gone.Participants.Select(a => a.Value));
            reply.Destroyed = record;
        }

        if (exported.Item is { } item)
        {
            reply.Item = Conversions.Out(item);
        }

        reply.Messages.AddRange(exported.Messages.Select(Conversions.Out));

        reply.Exchanges.AddRange(exported.Exchanges.Select(e => new Wire.ExchangeSnapshot
        {
            ExchangeId = e.ExchangeId,
            State = Conversions.Out(e.State),
            OpenedUtc = e.OpenedUtc.ToString("O", CultureInfo.InvariantCulture),
            ClosedUtc = e.ClosedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            Outcome = Conversions.Out(e.Outcome),
            SettledBy = e.SettledBy ?? 0
        }));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.RoadmapsReply> Roadmaps(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listed = await Console((console, _, ct) => console.ListRoadmapsAsync(ct), context).ConfigureAwait(false);

        var reply = new Wire.RoadmapsReply();
        reply.Roadmaps.AddRange(listed.Select(Conversions.Out));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.RoadmapReply> Roadmap(Wire.RoadmapRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var roadmap = await Console((console, _, ct) => console.GetRoadmapAsync(request.Id, ct), context)
            .ConfigureAwait(false);

        var reply = new Wire.RoadmapReply();

        if (roadmap is not null)
        {
            reply.Roadmap = Conversions.Out(roadmap);
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ProposalsReply> Proposals(Wire.ProposalsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var state = Conversions.In(request.State);

        // As headings when asked, since a list is read on every refresh and roadmap text has no bound (SPEC.md §10.2
        // `proposals`).
        var listed = request.Headings
            ? await Console((console, _, ct) => console.ListProposalHeadingsAsync(state, ct), context).ConfigureAwait(false)
            : await Console((console, _, ct) => console.ListProposalsAsync(state, ct), context).ConfigureAwait(false);

        var reply = new Wire.ProposalsReply();
        reply.Proposals.AddRange(listed.Select(Conversions.Out));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ProposalsReply> Proposal(Wire.ProposalRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // One proposal whole, in whatever state, or none: what a surface reads when the person opens one (§10.2 `proposal`).
        var one = await Console((console, _, ct) => console.GetProposalAsync(request.Id, ct), context).ConfigureAwait(false);

        var reply = new Wire.ProposalsReply();

        if (one is not null)
        {
            reply.Proposals.Add(Conversions.Out(one));
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ProposalCountReply> ProposalCount(Wire.ProposalsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var state = Conversions.In(request.State);
        var count = await Console((console, _, ct) => console.CountProposalsAsync(state, ct), context).ConfigureAwait(false);

        return new Wire.ProposalCountReply { Count = count };
    }

    /// <inheritdoc/>
    public override async Task<Wire.RoadmapChangeReply> ConfirmProposal(
        Wire.ConfirmProposalRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var content = Conversions.In(request.Content ?? new Wire.RoadmapContent());
        long? target = request.TargetRoadmap == 0 ? null : request.TargetRoadmap;

        // Zero on the wire is none named, which the store refuses for a revision (SPEC.md §10.2).
        int? revision = target is null || request.TargetRevision <= 0 ? null : request.TargetRevision;

        var change = await Console(
            (console, now, ct) => console.ConfirmProposalAsync(request.Id, content, target, now, ct, revision), context)
            .ConfigureAwait(false);

        return Conversions.Out(change);
    }

    /// <inheritdoc/>
    public override async Task<Wire.RoadmapChangeReply> DeclineProposal(
        Wire.ProposalRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var change = await Console((console, now, ct) => console.DeclineProposalAsync(request.Id, now, ct), context)
            .ConfigureAwait(false);

        return Conversions.Out(change);
    }

    /// <inheritdoc/>
    public override async Task<Wire.RoadmapChangeReply> EditRoadmap(
        Wire.EditRoadmapRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var content = Conversions.In(request.Content ?? new Wire.RoadmapContent());

        var change = await Console(
            (console, now, ct) => console.EditRoadmapAsync(request.Id, request.Revision, content, now, ct), context)
            .ConfigureAwait(false);

        return Conversions.Out(change);
    }

    /// <inheritdoc/>
    public override async Task<Wire.WaitingReply> DescribeWaiting(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var edges = await Console((console, _, ct) => console.DescribeWaitingAsync(ct), context).ConfigureAwait(false);

        var reply = new Wire.WaitingReply();
        reply.Edges.AddRange(edges.Select(e => new Wire.WaitingEdge
        {
            Waiter = e.Waiter.Value,
            WaitedOn = e.WaitedOn.Value,
            ExchangeId = e.ExchangeId
        }));

        return reply;
    }

    /// <inheritdoc/>
    public override Task<Wire.ImplementsReply> Implements(Wire.Empty request, ServerCallContext context)
    {
        var reply = new Wire.ImplementsReply
        {
            ProtocolVersion = ProtocolVersion.Current,
            BrokerVersion = Compatibility.VersionOf(typeof(OperatorsService).Assembly),
        };

        reply.StoreProblems.AddRange(_runtime.StoreProblems);

        return Task.FromResult(reply);
    }

    /// <inheritdoc/>
    public override async Task<Wire.ListConversationsReply> ListConversations(
        Wire.ListConversationsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var limit = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaximumPageSize);

        AgentName? participant = null;

        if (!string.IsNullOrEmpty(request.Participant))
        {
            if (!AgentName.TryCreate(request.Participant, out var named))
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"'{request.Participant}' is not an agent name, so it cannot narrow this list. Ask again " +
                    "without it to see everything."));
            }

            participant = named;
        }

        // The instant and the identifier at it, because the order breaks ties on the identifier and a resume
        // on the instant alone would skip conversations sharing one.
        var resumeAt = request.PageToken.Split(' ', 2);

        var summaries = await Console(
            (console, _, ct) => console.ListConversationsAsync(
                participant, Parse(request.SinceUtc), Parse(request.UntilUtc), limit, Parse(resumeAt[0]),
                resumeAt.Length > 1 ? resumeAt[1] : null, request.IncludeArchived, ct),
            context).ConfigureAwait(false);

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

            if (summary.Item is { } item)
            {
                row.Item = Conversions.Out(item);
            }

            row.Participants.AddRange(summary.Participants.Select(a => a.Value));
            reply.Conversations.Add(row);
        }

        reply.NextPageToken = summaries.Count < limit
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{summaries[^1].LastActivityUtc:O} {summaries[^1].Id}");

        return reply;
    }

    private static DateTimeOffset? Parse(string value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

    /// <inheritdoc/>
    public override async Task<Wire.PruneReply> Prune(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await Console((console, now, ct) => console.PruneAsync(_runtime.Retention, now, ct), context)
            .ConfigureAwait(false);

        return new Wire.PruneReply
        {
            Removed = result.Removed,
            HeldByFloor = result.HeldByFloor,
            Contended = result.Contended,
            Capped = result.Capped
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// As the person, which is why this is here and not on <c>Agents</c>: an agent places only as itself (SPEC.md §10.2).
    /// </remarks>
    public override Task<Wire.PlaceFileReply> PlacePersonsFile(
        IAsyncStreamReader<Wire.PlaceFileFrame> requestStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).PlaceAsPersonAsync(requestStream, context.CancellationToken);
    }

    /// <inheritdoc/>
    public override Task<Wire.FilesReply> ListSharedFiles(Wire.ListFilesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).ListAsync(request, forOperator: true, context.CancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Any placer's, which is why this is here and not on <c>Agents</c>: an agent removes only what it placed.
    /// </remarks>
    public override Task<Wire.RemoveFilesReply> RemoveFiles(Wire.RemoveFilesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).RemoveAsync(request, forOperator: true, context.CancellationToken);
    }

    /// <inheritdoc/>
    public override Task<Wire.PruneFilesReply> PruneFiles(Wire.Empty request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).PruneAsync(context.CancellationToken);
    }

    /// <inheritdoc/>
    public override Task<Wire.FileLocationReply> DescribeFileLocation(Wire.FileLocationRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).DescribeAsync(request, context.CancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<Wire.FileProblemsReply> ListFileProblems(
        Wire.ListFileProblemsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var problems = await _runtime.UseConnectionAsync(
            (connection, _, ct) => new Parley.Store.FileProblemRepository(connection).ListAsync(request.IncludeDismissed, ct),
            context.CancellationToken).ConfigureAwait(false);

        var reply = new Wire.FileProblemsReply { Machine = MachineName.Current, QuarantinePath = _runtime.Files.Quarantine };
        reply.Problems.AddRange(problems.Select(Conversions.Out));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.DidItReply> DismissFileProblem(
        Wire.DismissFileProblemRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var dismissed = await _runtime.UseConnectionAsync(
            (connection, now, ct) => new Parley.Store.FileProblemRepository(connection).DismissAsync(request.Id, now, ct),
            context.CancellationToken).ConfigureAwait(false);

        return new Wire.DidItReply { Changed = dismissed };
    }

    private Task<List<string>> RecordedFoldersAsync(CancellationToken cancellationToken) =>
        _runtime.UseConnectionAsync(
            async (connection, _, ct) => (await new Parley.Store.SharedFileRepository(connection)
                    .GetFoldersAsync(cancellationToken: ct).ConfigureAwait(false))
                .Select(f => f.Folder)
                .ToList(),
            cancellationToken);

    /// <summary>Deletes the folders on disk of conversations whose rows are gone.</summary>
    /// <param name="files">The file operations.</param>
    /// <param name="folders">The folders as they were recorded before the delete.</param>
    /// <returns>A task that completes when they are deleted.</returns>
    /// <remarks>
    /// <para>
    /// Checked against the store rather than assumed from the outcome, so a destroy or purge that deleted nothing —
    /// no such conversation, no such agent — can never take a folder that still belongs to one.
    /// </para>
    /// <para>
    /// **Never cancelled**, because it runs after the rows have gone: a call given up then would leave the folders on
    /// disk, deleted only by whatever next sweeps the location, though the operator was told their files went.
    /// </para>
    /// </remarks>
    private async Task DeleteFoldersWhoseRowsWentAsync(FileWork files, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }

        var gone = await _runtime.UseConnectionAsync(
            async (connection, _, ct) =>
            {
                var repository = new Parley.Store.SharedFileRepository(connection);
                var orphaned = new List<string>();

                foreach (var folder in folders)
                {
                    if (await repository.GetConversationForFolderAsync(folder, cancellationToken: ct).ConfigureAwait(false) is null)
                    {
                        orphaned.Add(folder);
                    }
                }

                return orphaned;
            },
            CancellationToken.None).ConfigureAwait(false);

        files.DeleteFolders(gone);
    }

    private Task<T> Console<T>(
        Func<OperatorConsole, DateTimeOffset, CancellationToken, Task<T>> work,
        ServerCallContext context) =>
        _runtime.UseConnectionAsync(
            (connection, now, ct) => work(new OperatorConsole(connection), now, ct),
            context.CancellationToken);

    private static AgentName Name(string value) =>
        AgentName.TryCreate(value, out var name)
            ? name
            : throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"'{value}' is not an agent name. Names are role@machine (SPEC.md §4.1.1)."));

    /// <summary>Reads the agent an operator operation acts on, refusing the facilitator.</summary>
    /// <param name="value">The name given.</param>
    /// <returns>The name.</returns>
    /// <remarks>
    /// Retiring, reinstating, purging, starting and accepting a line for the facilitator are each
    /// meaningless or harmful: it is never withdrawn, runs on no node, takes no turn and owns nothing (SPEC.md §14).
    /// Storage refuses the withdrawals too, but as a constraint rather than a sentence a person can read.
    /// </remarks>
    private static AgentName Subject(string value)
    {
        var agent = Name(value);

        return Parley.Core.Facilitation.Is(agent) ? throw NotAnAgent() : agent;
    }

    /// <summary>An agent an operation acts on, refusing Parley and the person with a sentence each.</summary>
    /// <param name="value">The name on the wire.</param>
    /// <returns>The agent.</returns>
    /// <remarks>
    /// <b>The person is refused by name, with a sentence</b> (SPEC.md §10.2), so `retire`, `purge`, `start` and
    /// `reinstate` give one answer to one fact rather than a raw constraint or a silent no-op.
    /// </remarks>
    private static AgentName Acted(string value)
    {
        var agent = Subject(value);

        return PersonSeat.Is(agent)
            ? throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"'{agent.Value}' is the person's seat. A person is asked, never started, retired or purged — " +
                "there is nothing to do to it."))
            : agent;
    }

    /// <summary>What an operator operation on an agent says when it is given the facilitator.</summary>
    /// <returns>The refusal.</returns>
    private static RpcException NotAnAgent() =>
        new(new Status(
            StatusCode.FailedPrecondition,
            "That is Parley, the project's own participant. It is not an agent: it runs nowhere, is never retired or " +
            "purged, and owns nothing, so there is nothing to do to it."));
}
