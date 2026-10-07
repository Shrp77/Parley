using System.Globalization;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Client;

/// <summary>
/// Broker V1 over gRPC: one project's broker, as <see cref="IParleyBrokerClient"/> describes it.
/// </summary>
/// <remarks>
/// <para>
/// A surface holds one of these per project and no other route to a broker. What it may do is what the
/// interface says, which is what makes SPEC.md §10.2 a contract rather than a description.
/// </para>
/// <para>
/// Mapping the wire to the domain is done here rather than shared with the server. The server's
/// conversions run in the process that owns the store; this runs in a process that must never see one.
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
        var said = await _operators.ImplementsAsync(new Wire.Empty(), cancellationToken: cancellationToken);
        return said.ProtocolVersion;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentRecord>> RegistryAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: cancellationToken);

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
            Presence(row.Presence),
            string.IsNullOrEmpty(row.OwnsAccepted) ? null : row.OwnsAccepted,
            AcceptedBy(row.OwnsSource),
            new TurnStanding(
                row.TurnsReported,
                Read(row.TurnRunningSinceUtc),
                Read(row.LastTurnEndedUtc),
                row.LastTurnClean,
                row.LastTurnPermissionDenials),
            row.OpenOutside))];
    }

    /// <summary>Reads how an accepted line was arrived at, off a registry row.</summary>
    /// <param name="source">The wire value.</param>
    /// <returns>The source, or <see langword="null"/> when nobody has accepted a line.</returns>
    /// <remarks>
    /// Unset is an answer here rather than a fault, unlike on a call that records one: most agents have no accepted
    /// line, and a row with none is the ordinary case.
    /// </remarks>
    private static OwnershipSource? AcceptedBy(Wire.OwnershipSource source) => source switch
    {
        Wire.OwnershipSource.Proposed => OwnershipSource.Proposed,
        Wire.OwnershipSource.Edited => OwnershipSource.Edited,
        Wire.OwnershipSource.Composed => OwnershipSource.Composed,
        Wire.OwnershipSource.Automatic => OwnershipSource.Automatic,
        _ => null
    };

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

        var reply = await _operators.ListConversationsAsync(request, cancellationToken: cancellationToken);

        var conversations = reply.Conversations.Select(row => new ConversationSummary(
            row.ConversationId,
            [.. row.Participants.Select(AgentName.Create)],
            row.Messages,
            Read(row.FirstActivityUtc) ?? DateTimeOffset.MinValue,
            Read(row.LastActivityUtc) ?? DateTimeOffset.MinValue,
            row.HasOpenExchange,
            row.Archived,
            ItemOf(row.Item)));

        return new ConversationPage(
            [.. conversations],
            string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken,
            reply.ArchivedOmitted);
    }

    /// <inheritdoc/>
    public async Task<bool> ArchiveAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = conversationId }, cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<bool> UnarchiveAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.UnarchiveAsync(
            new Wire.ConversationRequest { ConversationId = conversationId }, cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<DestroyOutcome> DestroyAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DestroyAsync(
            new Wire.ConversationRequest { ConversationId = conversationId }, cancellationToken: cancellationToken);

        return new DestroyOutcome(
            reply.Verdict == Wire.DestroyVerdict.Gone ? DestroyVerdict.Gone : DestroyVerdict.NoSuchConversation,
            [.. reply.Destroyed.Select(d => new PurgeToll(d.Table, d.Rows))]);
    }

    /// <inheritdoc/>
    public async Task<bool> StartAsync(AgentName agent, string because, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.StartAsync(
            new Wire.StartRequest { Agent = agent.Value, Because = because ?? string.Empty },
            cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<ConversationExport> ExportAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ExportAsync(
            new Wire.ExportRequest { ConversationId = conversationId }, cancellationToken: cancellationToken);

        var destroyed = reply.Destroyed is { } gone
            ? new DestroyedConversation(
                gone.ConversationId,
                Read(gone.DestroyedUtc) ?? DateTimeOffset.MinValue,
                gone.Messages,
                [.. gone.Participants.Select(AgentName.Create)])
            : null;

        var messages = reply.Messages.Select(m => new Message(
            m.Id,
            m.ExchangeId,
            Type(m.Type),
            AgentName.Create(m.From),
            AgentName.Create(m.To),
            m.ResponderModel,
            m.PayloadJson,
            m.ConversationId,
            Read(m.CreatedUtc) ?? DateTimeOffset.MinValue,
            Read(m.DeliveredUtc)));

        var exchanges = reply.Exchanges.Select(e => new ExchangeSnapshot(
            e.ExchangeId,
            e.State == Wire.ExchangeState.Open ? ExchangeState.Open : ExchangeState.Closed,
            Read(e.OpenedUtc) ?? DateTimeOffset.MinValue,
            Read(e.ClosedUtc),
            e.Outcome switch
            {
                Wire.ExchangeOutcome.Complete => ExchangeOutcome.Complete,
                Wire.ExchangeOutcome.Blocked => ExchangeOutcome.Blocked,
                Wire.ExchangeOutcome.Closed => ExchangeOutcome.Closed,
                Wire.ExchangeOutcome.Settled => ExchangeOutcome.Settled,
                _ => null
            },
            e.SettledBy == 0 ? null : e.SettledBy));

        return new ConversationExport([.. messages], [.. exchanges], destroyed, ItemOf(reply.Item));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WaitingEdge>> WaitingAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DescribeWaitingAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return [.. reply.Edges.Select(e => new WaitingEdge(
            AgentName.Create(e.Waiter), AgentName.Create(e.WaitedOn), e.ExchangeId))];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SizeRefusal>> RefusalsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DescribeRefusalsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

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
    public async Task<ClarifyReport> ClarificationsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DescribeClarificationsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new ClarifyReport(
            [.. reply.Distribution.Select(b => new ClarifyDepthBucket(b.Depth, b.Exchanges))],
            reply.ExchangesAtCap,
            reply.LowestCapInForce,
            reply.HighestCapInForce,
            reply.Results,
            reply.ResultsWithMixedOutcomes);
    }

    /// <inheritdoc/>
    public async Task<ResultsReport> ResultsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DescribeResultsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new ResultsReport(
            [.. reply.Outcomes
                .Where(o => o.Outcome != Wire.Outcome.Unspecified)
                .Select(o => new OutcomeCount(o.Agent, o.ResponderModel, Outcome(o.Outcome), o.Items))],
            [.. reply.Blockers
                .Where(b => b.Kind != Wire.BlockerKind.Unspecified)
                .Select(b => new BlockerCount(b.Agent, Blocker(b.Kind), b.Items))],
            [.. reply.Sources.Select(s => new SourceCount(s.Agent, s.Source, s.Items))],
            [.. reply.Closures.Select(c => new ClosureCount(c.Agent, c.ByConsensus, c.ByPerson))]);
    }

    /// <inheritdoc/>
    public async Task<FlagReport> FlagsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DescribeFlagsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new FlagReport(
            [.. reply.Flags.Select(f => new Flag(
                f.Kind switch
                {
                    Wire.FlagKind.NoProgress => FlagKind.NoProgress,
                    Wire.FlagKind.Retired => FlagKind.Retired,
                    Wire.FlagKind.NotVoted => FlagKind.NotVoted,
                    Wire.FlagKind.PermissionRefused => FlagKind.PermissionRefused,
                    _ => FlagKind.NotCollected
                },
                f.ExchangeId,
                f.ConversationId,
                AgentName.Create(f.WaitingOn),
                AgentName.Create(f.Waiting),
                Read(f.SinceUtc) ?? DateTimeOffset.MinValue))],
            [.. reply.Cycles.Select(c => new RecordedCycle(
                c.ExchangeId,
                c.ConversationId,
                [.. c.Path.Where(name => AgentName.TryCreate(name, out _)).Select(AgentName.Create)],
                Read(c.RecordedUtc) ?? DateTimeOffset.MinValue))]);
    }

    /// <inheritdoc/>
    public async Task<bool> RetireAsync(AgentName agent, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.RetireAsync(new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);
        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<OwnershipRecord> RecordOwnershipAsync(
        AgentName agent, OwnershipAcceptance acceptance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptance);

        var reply = await _operators.RecordOwnershipAsync(
            new Wire.RecordOwnershipRequest
            {
                Agent = agent.Value,
                Statement = acceptance.Statement,
                Source = Source(acceptance.Source),
                ProposedLength = acceptance.ProposedLength ?? 0,
                ConversationId = acceptance.Conversation ?? string.Empty
            },
            cancellationToken: cancellationToken);

        return new OwnershipRecord(
            reply.Recorded,
            string.IsNullOrEmpty(reply.Problem) ? null : reply.Problem,
            string.IsNullOrEmpty(reply.LiveStatement) ? null : reply.LiveStatement)
        {
            Pending = reply.Pending
        };
    }

    /// <inheritdoc/>
    public async Task<FacilitationReport> FacilitationAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.FacilitationAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new FacilitationReport(reply.AutoAccept, [.. reply.Questions.Select(Question)]);
    }

    /// <inheritdoc/>
    public async Task<bool> SetAutoAcceptAsync(bool on, CancellationToken cancellationToken = default) =>
        (await _operators.SetAutoAcceptAsync(new Wire.SetAutoAcceptRequest { On = on }, cancellationToken: cancellationToken)).On;

    /// <inheritdoc/>
    public async Task<FacilitationAction> AskAgainAsync(AgentName agent, CancellationToken cancellationToken = default) =>
        Action(await _operators.AskAgainAsync(new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken));

    /// <inheritdoc/>
    public async Task<FacilitationAction> ReplyToAskedBackAsync(
        AgentName agent, string text, CancellationToken cancellationToken = default) =>
        Action(await _operators.ReplyToAskedBackAsync(
            new Wire.ReplyToAskedBackRequest { Agent = agent.Value, Text = text ?? string.Empty },
            cancellationToken: cancellationToken));

    /// <inheritdoc/>
    public async Task<FacilitationAction> DiscardAnswerAsync(AgentName agent, CancellationToken cancellationToken = default) =>
        Action(await _operators.DiscardAnswerAsync(new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken));

    private static FacilitationAction Action(Wire.FacilitationActionReply reply) =>
        reply.Done ? FacilitationAction.Did : FacilitationAction.Not(
            string.IsNullOrEmpty(reply.Problem) ? "the broker did not say why." : reply.Problem);

    /// <summary>Reads one of Parley's questions off the wire.</summary>
    /// <remarks>
    /// A state this client does not know is read as asked rather than refused: the review should still list the agent,
    /// and asked is the one state that promises nothing about an answer.
    /// </remarks>
    private static OwnershipQuestion Question(Wire.OwnershipQuestion question) => new(
        AgentName.Create(question.Agent),
        question.ConversationId,
        question.ExchangeId,
        question.State switch
        {
            Wire.QuestionState.AskedBack => QuestionState.AskedBack,
            Wire.QuestionState.Answered => QuestionState.Answered,
            Wire.QuestionState.Waiting => QuestionState.Waiting,
            Wire.QuestionState.Accepted => QuestionState.Accepted,
            Wire.QuestionState.Discarded => QuestionState.Discarded,
            _ => QuestionState.Asked
        },
        string.IsNullOrEmpty(question.Answer) ? null : question.Answer,
        string.IsNullOrEmpty(question.Flattened) ? null : question.Flattened,
        string.IsNullOrEmpty(question.WaitingBecause) ? null : question.WaitingBecause,
        DateTimeOffset.Parse(question.AskedUtc, System.Globalization.CultureInfo.InvariantCulture),
        string.IsNullOrEmpty(question.SettledUtc)
            ? null
            : DateTimeOffset.Parse(question.SettledUtc, System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    public async Task<bool> ReinstateAsync(AgentName agent, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ReinstateAsync(new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);
        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<PurgeOutcome> PurgeAsync(AgentName agent, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.PurgeAsync(new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);

        return new PurgeOutcome(
            reply.Verdict == Wire.PurgeVerdict.Gone ? PurgeVerdict.Gone : PurgeVerdict.NotRegistered,
            [.. reply.Destroyed.Select(d => new PurgeToll(d.Table, d.Rows))]);
    }

    /// <inheritdoc/>
    public async Task<bool> ForceConcludeAsync(
        string conversationId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ForceConcludeAsync(
            new Wire.ForceConcludeRequest { ConversationId = conversationId, Reason = reason ?? string.Empty },
            cancellationToken: cancellationToken);

        return reply.Changed;
    }

    /// <inheritdoc/>
    public async Task<PruneResult> PruneAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.PruneAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new PruneResult(reply.Removed, reply.HeldByFloor, reply.Contended, reply.Capped);
    }

    /// <inheritdoc/>
    public async Task<SharedFileListing> ListSharedFilesAsync(
        string? conversationId = null,
        int pageSize = 0,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ListSharedFilesAsync(
            new Wire.ListFilesRequest
            {
                ConversationId = conversationId ?? string.Empty,
                PageSize = pageSize,
                PageToken = pageToken ?? string.Empty
            },
            cancellationToken: cancellationToken);

        return new SharedFileListing(
            new SharedFilePage(
                [.. reply.Files.Select(SharedFileOf)],
                [.. reply.Totals.Select(t => new ConversationFilesTotal(t.ConversationId, t.Files, t.Bytes))],
                reply.TotalFiles,
                reply.TotalBytes,
                string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken),
            [.. reply.Placements.Select(p => new FilePlacementCount(
                p.Agent, p.Placements, p.Bytes, Read(p.LastUtc) ?? DateTimeOffset.MinValue))],
            Math.Max(0, reply.FilesNotListed));
    }

    /// <inheritdoc/>
    public async Task<RemoveFilesOutcome> RemoveFilesAsync(
        IReadOnlyList<FileRef> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var request = new Wire.RemoveFilesRequest();
        request.Files.AddRange(files.Select(f => new Wire.FileRef { ConversationId = f.ConversationId ?? string.Empty, Name = f.Name }));

        var reply = await _operators.RemoveFilesAsync(request, cancellationToken: cancellationToken);

        return new RemoveFilesOutcome(
            [.. reply.Removed.Select(r => new FileRef(Blank(r.ConversationId), r.Name))],
            [.. reply.Refused.Select(RefusalOf)],
            reply.BytesFreed,
            RejectionOf(reply.Rejection));
    }

    /// <inheritdoc/>
    public async Task<PruneFilesOutcome> PruneFilesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.PruneFilesAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return new PruneFilesOutcome(reply.FilesRemoved, reply.BytesFreed);
    }

    /// <inheritdoc/>
    public async Task<FileLocationAnswer> DescribeFileLocationAsync(
        string? conversationId = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DescribeFileLocationAsync(
            new Wire.FileLocationRequest { ConversationId = conversationId ?? string.Empty, Name = name ?? string.Empty },
            cancellationToken: cancellationToken);

        return reply.Ok
            ? new FileLocationAnswer(new FileLocation(reply.Machine, reply.Path), null)
            : new FileLocationAnswer(null, RefusalOf(reply.Refusal ?? new Wire.FileRefusal()));
    }

    /// <summary>Reads a shared file from the wire.</summary>
    /// <param name="view">The wire view.</param>
    /// <returns>The file.</returns>
    /// <exception cref="InvalidOperationException">The broker sent a name or hash no file can have.</exception>
    /// <remarks>
    /// Refused rather than shown, because a name the rules refuse is text this surface did not validate, reaching a
    /// screen through the one field meant to carry a checked name.
    /// </remarks>
    private static SharedFile SharedFileOf(Wire.SharedFileView view) =>
        new(
            view.ConversationId,
            view.Folder,
            SharedFileName.TryCreate(view.Name, out var name)
                ? name
                : throw new InvalidOperationException("The broker listed a file under a name no file can have."),
            view.SizeBytes,
            ContentHash.TryParse(view.Sha256, out var hash)
                ? hash
                : throw new InvalidOperationException("The broker listed a file with a hash that is not one."),
            string.IsNullOrEmpty(view.PlacedBy) ? null : AgentName.Create(view.PlacedBy),
            Read(view.PlacedUtc) ?? DateTimeOffset.MinValue,
            Read(view.ChangedUtc) ?? DateTimeOffset.MinValue);

    private static FileRefusal RefusalOf(Wire.FileRefusal refusal) =>
        new(
            new FileRef(Blank(refusal.File?.ConversationId), refusal.File?.Name ?? string.Empty),
            RejectionOf(refusal.Rejection),
            Blank(refusal.PlacedBy),
            [.. refusal.Conversations],
            refusal.ConversationsMore,
            Blank(refusal.Detail));

    private static FileRejection RejectionOf(Wire.FileRejection rejection) => rejection switch
    {
        Wire.FileRejection.None => FileRejection.None,
        Wire.FileRejection.BadName => FileRejection.BadName,
        Wire.FileRejection.NamePlacedByAnother => FileRejection.NamePlacedByAnother,
        Wire.FileRejection.NoSuchFile => FileRejection.NoSuchFile,
        Wire.FileRejection.AmbiguousName => FileRejection.AmbiguousName,
        Wire.FileRejection.ConversationNotAssignable => FileRejection.ConversationNotAssignable,
        Wire.FileRejection.ConversationClosed => FileRejection.ConversationClosed,
        Wire.FileRejection.Changed => FileRejection.Changed,
        Wire.FileRejection.Incomplete => FileRejection.Incomplete,
        Wire.FileRejection.NoSpace => FileRejection.NoSpace,
        Wire.FileRejection.ExtensionNotAllowed => FileRejection.ExtensionNotAllowed,
        Wire.FileRejection.NotFetched => FileRejection.NotFetched,
        Wire.FileRejection.TooManyNames => FileRejection.TooManyNames,
        Wire.FileRejection.SafeNameTaken => FileRejection.SafeNameTaken,
        _ => throw new InvalidOperationException(
            $"The broker refused a file operation for a reason this build does not know ({rejection}). It speaks a different protocol.")
    };

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Render(DateTimeOffset? instant) =>
        instant?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    private static DateTimeOffset? Read(string value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

    private static AgentTier Tier(Wire.AgentTier tier) => tier switch
    {
        Wire.AgentTier.Full => AgentTier.Full,
        Wire.AgentTier.Pull => AgentTier.Pull,
        Wire.AgentTier.Headless => AgentTier.Headless,
        _ => AgentTier.Pull
    };

    private static AgentStatus Status(Wire.AgentStatus status) =>
        status == Wire.AgentStatus.Retired ? AgentStatus.Retired : AgentStatus.Active;

    private static AgentPresence Presence(Wire.AgentPresence presence) => presence switch
    {
        Wire.AgentPresence.Running => AgentPresence.Running,
        Wire.AgentPresence.Startable => AgentPresence.Startable,
        Wire.AgentPresence.Here => AgentPresence.Here,
        Wire.AgentPresence.Away => AgentPresence.Away,
        _ => AgentPresence.Stranded
    };

    /// <summary>Reads a message type from the wire.</summary>
    /// <param name="type">The wire value.</param>
    /// <returns>The type.</returns>
    /// <exception cref="InvalidOperationException">The broker sent a type this build does not know.</exception>
    /// <remarks>
    /// Refused rather than defaulted: a surface that rendered an unknown message as some known kind would
    /// be putting words into the transcript that nobody wrote.
    /// </remarks>
    internal static MessageType Type(Wire.MessageType type) => type switch
    {
        Wire.MessageType.Request => MessageType.Request,
        Wire.MessageType.Clarify => MessageType.Clarify,
        Wire.MessageType.Result => MessageType.Result,
        _ => throw new InvalidOperationException(
            $"The broker sent a message type this build does not know ({type}). It speaks a different protocol.")
    };

    /// <summary>Puts how a person arrived at an ownership line onto the wire.</summary>
    /// <param name="source">Which of the three routes produced it.</param>
    /// <returns>The wire value.</returns>
    /// <remarks>
    /// Never <c>Unspecified</c>, which is what an unset field arrives as. Sending it would let the
    /// broker refuse the call for a value this client chose rather than one a person did.
    /// </remarks>
    private static Wire.OwnershipSource Source(OwnershipSource source) => source switch
    {
        OwnershipSource.Proposed => Wire.OwnershipSource.Proposed,
        OwnershipSource.Edited => Wire.OwnershipSource.Edited,
        OwnershipSource.Composed => Wire.OwnershipSource.Composed,
        OwnershipSource.Automatic => Wire.OwnershipSource.Automatic,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a defined source.")
    };

    // ---- roadmaps (SPEC.md section 5.7)

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RoadmapSummary>> RoadmapsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _operators.RoadmapsAsync(new Wire.Empty(), cancellationToken: cancellationToken);

        return [.. reply.Roadmaps.Select(r => new RoadmapSummary(
            r.Id, r.Number, r.Title, r.Revision, r.LiveItems, r.StartedItems))];
    }

    /// <inheritdoc/>
    public async Task<Roadmap?> RoadmapAsync(long id, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.RoadmapAsync(
            new Wire.RoadmapRequest { Id = id }, cancellationToken: cancellationToken);

        if (reply.Roadmap is not { } roadmap)
        {
            return null;
        }

        return new Roadmap(
            roadmap.Id,
            roadmap.Number,
            roadmap.Title,
            roadmap.Origin == Wire.RoadmapOrigin.Proposed ? RoadmapOrigin.Proposed : RoadmapOrigin.Edited,
            string.IsNullOrEmpty(roadmap.Proposer) ? null : roadmap.Proposer,
            roadmap.Revision,
            [.. roadmap.Items.Select(i => new RoadmapItem(
                i.Id,
                i.Number,
                i.Position,
                i.Text,
                string.IsNullOrEmpty(i.IntendedAgent) ? null : AgentName.Create(i.IntendedAgent),
                [.. i.Needs],
                i.ConversationName,
                i.RemovedRevision == 0 ? null : i.RemovedRevision,
                i.Facts is { } facts
                    ? new ItemFacts(
                        facts.ConversationExists,
                        facts.OpenExchanges,
                        facts.Collected,
                        facts.CompleteExchanges,
                        facts.BlockedExchanges,
                        facts.ClosedExchanges,
                        string.IsNullOrEmpty(facts.Blocker) ? null : facts.Blocker,
                        string.IsNullOrEmpty(facts.ClosedBy) ? null : facts.ClosedBy,
                        facts.Destroyed,
                        string.IsNullOrEmpty(facts.FirstSentText) ? null : facts.FirstSentText,
                        [.. facts.OpenExchangeIds])
                    : null))]);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RoadmapProposal>> ProposalsAsync(
        ProposalState? state = ProposalState.Waiting, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ProposalsAsync(
            new Wire.ProposalsRequest { State = state is { } s ? ProposalStateOut(s) : Wire.ProposalState.Unspecified },
            cancellationToken: cancellationToken);

        return [.. reply.Proposals.Select(p => new RoadmapProposal(
            p.Id,
            AgentName.Create(p.Proposer),
            string.IsNullOrEmpty(p.SourceConversation) ? null : p.SourceConversation,
            string.IsNullOrEmpty(p.TargetTitle) ? null : p.TargetTitle,
            ContentIn(p.Content),
            p.Size,
            ProposalStateIn(p.State),
            Read(p.CreatedUtc) ?? DateTimeOffset.MinValue,
            Read(p.DecidedUtc)))];
    }

    /// <inheritdoc/>
    public async Task<RoadmapChange> ConfirmProposalAsync(
        long id, RoadmapContent content, long? targetRoadmap, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = id, Content = ContentOut(content), TargetRoadmap = targetRoadmap ?? 0 },
            cancellationToken: cancellationToken);

        return ChangeIn(reply);
    }

    /// <inheritdoc/>
    public async Task<RoadmapChange> DeclineProposalAsync(long id, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.DeclineProposalAsync(
            new Wire.ProposalRequest { Id = id }, cancellationToken: cancellationToken);

        return ChangeIn(reply);
    }

    /// <inheritdoc/>
    public async Task<RoadmapChange> EditRoadmapAsync(
        long id, int revision, RoadmapContent content, CancellationToken cancellationToken = default)
    {
        var reply = await _operators.EditRoadmapAsync(
            new Wire.EditRoadmapRequest { Id = id, Revision = revision, Content = ContentOut(content) },
            cancellationToken: cancellationToken);

        return ChangeIn(reply);
    }

    private static RoadmapItemRef? ItemOf(Wire.RoadmapItemRef? item) =>
        item is null
            ? null
            : new RoadmapItemRef(item.RoadmapId, item.RoadmapNumber, item.Title, item.ItemNumber, item.Removed);

    private static RoadmapChange ChangeIn(Wire.RoadmapChangeReply reply) => new(
        reply.Done,
        string.IsNullOrEmpty(reply.Problem) ? null : reply.Problem,
        reply.RoadmapId == 0 ? null : reply.RoadmapId,
        reply.Revision == 0 ? null : reply.Revision,
        reply.Origin switch
        {
            Wire.RoadmapOrigin.Proposed => RoadmapOrigin.Proposed,
            Wire.RoadmapOrigin.Edited => RoadmapOrigin.Edited,
            _ => null,
        });

    private static Wire.RoadmapContent ContentOut(RoadmapContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var wire = new Wire.RoadmapContent { Title = content.Title };

        foreach (var item in content.Items)
        {
            var draft = new Wire.RoadmapItemDraft
            {
                Number = item.Number ?? 0,
                Text = item.Text,
                IntendedAgent = item.IntendedAgent?.Value ?? string.Empty,
            };

            draft.Needs.AddRange(item.Needs);
            wire.Items.Add(draft);
        }

        return wire;
    }

    private static RoadmapContent ContentIn(Wire.RoadmapContent content) => new(
        content.Title,
        [.. content.Items.Select(i => new RoadmapItemDraft(
            i.Number == 0 ? null : i.Number,
            i.Text,
            string.IsNullOrEmpty(i.IntendedAgent) ? null : AgentName.Create(i.IntendedAgent),
            [.. i.Needs]))]);

    private static Wire.ProposalState ProposalStateOut(ProposalState state) => state switch
    {
        ProposalState.Waiting => Wire.ProposalState.Waiting,
        ProposalState.Confirmed => Wire.ProposalState.Confirmed,
        ProposalState.Declined => Wire.ProposalState.Declined,
        ProposalState.Superseded => Wire.ProposalState.Superseded,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a proposal state."),
    };

    /// <summary>Reads a proposal's state from the wire, refusing one this build does not know.</summary>
    /// <param name="state">The wire value.</param>
    /// <returns>The state.</returns>
    /// <exception cref="InvalidOperationException">The broker sent a state this build does not know.</exception>
    /// <remarks>
    /// Refused rather than defaulted, as a message type is: a surface that showed an unknown state as a known one would
    /// be telling a person a proposal was waiting that had been decided, or the reverse.
    /// </remarks>
    private static ProposalState ProposalStateIn(Wire.ProposalState state) => state switch
    {
        Wire.ProposalState.Waiting => ProposalState.Waiting,
        Wire.ProposalState.Confirmed => ProposalState.Confirmed,
        Wire.ProposalState.Declined => ProposalState.Declined,
        Wire.ProposalState.Superseded => ProposalState.Superseded,
        _ => throw new InvalidOperationException(
            $"The broker sent a proposal state this build does not know ({state}). It speaks a different protocol."),
    };

    private static Outcome Outcome(Wire.Outcome outcome) => outcome switch
    {
        Wire.Outcome.Done => Core.Outcome.Done,
        Wire.Outcome.Answered => Core.Outcome.Answered,
        Wire.Outcome.Acknowledged => Core.Outcome.Acknowledged,
        Wire.Outcome.Blocked => Core.Outcome.Blocked,
        _ => Core.Outcome.Closed
    };

    private static BlockerKind Blocker(Wire.BlockerKind kind) => kind switch
    {
        Wire.BlockerKind.Conflict => BlockerKind.Conflict,
        Wire.BlockerKind.Permission => BlockerKind.Permission,
        _ => BlockerKind.MissingInformation
    };
}
