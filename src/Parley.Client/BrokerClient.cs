using System.Globalization;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Client;

/// <summary>
/// Broker V1 over gRPC: one project's broker, as <see cref="IParleyBrokerClient"/> describes it.
/// </summary>
/// <remarks>
/// <para>
/// The console holds one of these per project and no other route to a broker. What it may do is
/// what the interface says, which is what makes SPEC.md §10.2 a contract rather than a
/// description — a page that needed something not on here would be a page asking for something no
/// broker promises.
/// </para>
/// <para>
/// Mapping the wire to the domain is done here rather than shared with the server. The server's
/// conversions run in the process that owns the store; this runs in a process that must never
/// see one, and a shared assembly would be a path between the two that §4 spends its length
/// denying.
/// </para>
/// </remarks>
/// <param name="operators">The operator client for one project's broker.</param>
public sealed class BrokerClient(Wire.Operators.OperatorsClient operators) : IParleyBrokerClient
{
    private readonly Wire.Operators.OperatorsClient _operators = operators
        ?? throw new ArgumentNullException(nameof(operators));

    /// <inheritdoc/>
    public async Task<string> ImplementsAsync(CancellationToken cancellationToken = default)
    {
        var said = await _operators
            .ImplementsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return said.ProtocolVersion;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentRecord>> RegistryAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators
            .DescribeRegistryAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return [.. reply.Rows.Select(row => new AgentRecord(
            AgentName.Create(row.Name),
            row.Adapter,
            Tier(row.Tier),
            string.IsNullOrEmpty(row.Workspace) ? null : row.Workspace,
            string.IsNullOrEmpty(row.SessionRef) ? null : row.SessionRef,
            null,
            row.Connected,
            Status(row.Status),
            string.IsNullOrEmpty(row.Owns) ? null : row.Owns,
            Presence(row.Presence)))];
    }

    /// <inheritdoc/>
    public async Task<ConversationPage> ConversationsAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        string? pageToken = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var request = new Wire.ListConversationsRequest
        {
            Participant = participant?.Value ?? string.Empty,
            SinceUtc = Render(sinceUtc),
            UntilUtc = Render(untilUtc),
            PageToken = pageToken ?? string.Empty,
            IncludeArchived = includeArchived
        };

        var reply = await _operators
            .ListConversationsAsync(request, cancellationToken: cancellationToken);

        var conversations = reply.Conversations.Select(row => new ConversationSummary(
            row.ConversationId,
            [.. row.Participants.Select(AgentName.Create)],
            row.Messages,
            Read(row.FirstActivityUtc) ?? DateTimeOffset.MinValue,
            Read(row.LastActivityUtc) ?? DateTimeOffset.MinValue,
            row.HasOpenExchange,
            row.Archived));

        return new ConversationPage(
            [.. conversations],
            string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken,
            reply.ArchivedOmitted);
    }

    /// <inheritdoc/>
    public async Task<bool> ArchiveAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = conversationId },
            cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<bool> UnarchiveAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.UnarchiveAsync(
            new Wire.ConversationRequest { ConversationId = conversationId },
            cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<DestroyOutcome> DestroyAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DestroyAsync(
            new Wire.ConversationRequest { ConversationId = conversationId },
            cancellationToken: cancellationToken);

        return new DestroyOutcome(
            reply.Verdict == Wire.DestroyVerdict.Gone
                ? DestroyVerdict.Gone
                : DestroyVerdict.NoSuchConversation,
            [.. reply.Destroyed.Select(d => new PurgeToll(d.Table, d.Rows))]);
    }

    /// <inheritdoc/>
    public async Task<bool> StartAsync(
        AgentName agent, string because, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.StartAsync(
            new Wire.StartRequest { Agent = agent.Value, Because = because ?? string.Empty },
            cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<ConversationExport> ExportAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ExportAsync(
            new Wire.ExportRequest { ConversationId = conversationId },
            cancellationToken: cancellationToken);

        var destroyed = reply.Destroyed is { } gone
            ? new DestroyedConversation(
                gone.ConversationId,
                Read(gone.DestroyedUtc) ?? DateTimeOffset.MinValue,
                gone.Messages,
                [.. gone.Participants.Select(AgentName.Create)])
            : null;

        var messages = reply.Messages.Select(m => new Message(
            m.Id,
            m.ExchangeId == 0 ? null : m.ExchangeId,
            Type(m.Type),
            AgentName.Create(m.From),
            AgentName.Create(m.To),
            m.ResponderModel,
            m.PayloadJson,
            string.IsNullOrEmpty(m.ConversationId) ? null : m.ConversationId,
            Read(m.CreatedUtc) ?? DateTimeOffset.MinValue,
            Read(m.DeliveredUtc)));

        var exchanges = reply.Exchanges.Select(e => new ExchangeSnapshot(
            e.ExchangeId,
            State(e.State),
            Read(e.OpenedUtc) ?? DateTimeOffset.MinValue,
            Read(e.ClosedUtc)));

        return new ConversationExport([.. messages], [.. exchanges], destroyed);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WaitingEdge>> WaitingAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators
            .DescribeWaitingAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return [.. reply.Edges.Select(e => new WaitingEdge(
            AgentName.Create(e.Waiter), AgentName.Create(e.WaitedOn), e.ExchangeId))];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SizeRefusal>> RefusalsAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators
            .DescribeRefusalsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return [.. reply.Refusals.Select(r => new SizeRefusal(
            AgentName.Create(r.Agent),
            Type(r.Type),
            r.Refusals,
            r.Worst,
            r.Total,
            r.Cap,
            Read(r.LastUtc) ?? DateTimeOffset.MinValue))];
    }

    /// <inheritdoc/>
    public async Task<ClarifyReport> ClarificationsAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators
            .DescribeClarificationsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new ClarifyReport(
            [.. reply.Distribution.Select(b => new ClarifyDepthBucket(b.Depth, b.Exchanges))],
            reply.ExchangesAtCap,
            reply.LowestCapInForce,
            reply.HighestCapInForce,
            reply.Answers,
            reply.AnswersResolvingSubQuestionsDifferently);
    }

    /// <inheritdoc/>
    public async Task<ReferralReport> ReferralsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators
            .DescribeReferralsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new ReferralReport(
            [.. reply.Pairs.Select(p => new ReferralPair(
                p.Asker, p.Answerer, p.Closed, p.OutOfScope))],
            [.. reply.Targets.Select(t => new ReferralTarget(
                t.Asker, t.Answerer, t.RedirectedTo, t.Referrals, t.LastUtc))]);
    }

    /// <inheritdoc/>
    public async Task<NotifyVolumeReport> NotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators
            .DescribeNotificationsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new NotifyVolumeReport(
            [.. reply.Flows.Select(f => new NotifyFlow(
                f.From, f.To, f.Sent, f.Characters, f.Largest, f.FirstUtc, f.LastUtc))]);
    }

    /// <inheritdoc/>
    public async Task<InstructionPage> InstructionsAsync(
        AgentName? agent = null,
        long? instructionId = null,
        int pageSize = 0,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators.InstructionsAsync(
            new Wire.InstructionsRequest
            {
                Agent = agent?.Value ?? string.Empty,
                InstructionId = instructionId ?? 0,
                PageSize = pageSize,
                PageToken = pageToken ?? string.Empty
            },
            cancellationToken: cancellationToken);

        return new InstructionPage(
            [.. reply.Instructions.Select(i => new InstructionRecord(
                i.Id,
                AgentName.Create(i.Agent),
                i.Text,
                Outcome(i.Outcome),
                string.IsNullOrEmpty(i.Reply) ? null : i.Reply,
                DateTimeOffset.Parse(i.IssuedUtc, CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(i.SettledUtc)
                    ? null
                    : DateTimeOffset.Parse(i.SettledUtc, CultureInfo.InvariantCulture)))],
            string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken);
    }

    /// <inheritdoc/>
    public async Task<InstructionResult> InstructAsync(
        AgentName agent, string text, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.InstructAsync(
            new Wire.InstructRequest { Agent = agent.Value, Text = text },
            cancellationToken: cancellationToken);

        return new InstructionResult(
            Outcome(reply.Outcome),
            string.IsNullOrEmpty(reply.Reply) ? null : reply.Reply,
            reply.InstructionId);
    }

    /// <inheritdoc/>
    public async Task<bool> RetireAsync(
        AgentName agent, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.RetireAsync(
            new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<bool> ReinstateAsync(
        AgentName agent, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ReinstateAsync(
            new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<PurgeOutcome> PurgeAsync(
        AgentName agent, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.PurgeAsync(
            new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);

        return new PurgeOutcome(
            reply.Verdict == Wire.PurgeVerdict.Gone ? PurgeVerdict.Gone : PurgeVerdict.NotRegistered,
            [.. reply.Destroyed.Select(d => new PurgeToll(d.Table, d.Rows))]);
    }

    /// <inheritdoc/>
    public async Task<bool> ForceConcludeAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ForceConcludeAsync(
            new Wire.ForceConcludeRequest { ConversationId = conversationId },
            cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<PruneResult> PruneAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators
            .PruneAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new PruneResult(reply.Removed, reply.HeldByFloor, reply.Contended, reply.Capped);
    }

    /// <summary>Renders an instant for the wire, where empty means absent.</summary>
    /// <param name="instant">The instant, or null.</param>
    /// <returns>The rendered form, or an empty string.</returns>
    private static string Render(DateTimeOffset? instant) =>
        instant?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Reads an instant from the wire, where empty means absent.</summary>
    /// <param name="value">The rendered instant, or empty.</param>
    /// <returns>The instant, or null.</returns>
    private static DateTimeOffset? Read(string value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

    private static AgentTier Tier(Wire.AgentTier tier) => tier switch
    {
        Wire.AgentTier.Full => AgentTier.Full,
        Wire.AgentTier.Pull => AgentTier.Pull,
        Wire.AgentTier.Headless => AgentTier.Headless,
        _ => AgentTier.Pull
    };

    private static AgentStatus Status(Wire.AgentStatus status) => status switch
    {
        Wire.AgentStatus.Retired => AgentStatus.Retired,
        _ => AgentStatus.Active
    };

    private static AgentPresence Presence(Wire.AgentPresence presence) => presence switch
    {
        Wire.AgentPresence.Running => AgentPresence.Running,
        Wire.AgentPresence.Startable => AgentPresence.Startable,
        _ => AgentPresence.Stranded
    };

    private static MessageType Type(Wire.MessageType type) => type switch
    {
        Wire.MessageType.Question => MessageType.Question,
        Wire.MessageType.Clarify => MessageType.Clarify,
        Wire.MessageType.Answer => MessageType.Answer,
        Wire.MessageType.Notify => MessageType.Notify,
        _ => MessageType.Ghost
    };

    private static ExchangeState State(Wire.ExchangeState state) => state switch
    {
        Wire.ExchangeState.Open => ExchangeState.Open,
        Wire.ExchangeState.Orphaned => ExchangeState.Orphaned,
        _ => ExchangeState.Closed
    };

    private static InstructionOutcome Outcome(Wire.InstructionOutcome outcome) => outcome switch
    {
        Wire.InstructionOutcome.Taken => InstructionOutcome.Taken,
        Wire.InstructionOutcome.Declined => InstructionOutcome.Declined,
        Wire.InstructionOutcome.Undeliverable => InstructionOutcome.Undeliverable,
        Wire.InstructionOutcome.Failed => InstructionOutcome.Failed,
        _ => InstructionOutcome.Pending
    };
}
