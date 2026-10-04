using System.Globalization;
using Parley.Core;
using Parley.Store;
using Wire = Parley.Transport;

namespace Parley.Server;

/// <summary>
/// Translates between the domain types and the wire contract of <c>proto/parley.proto</c>.
/// </summary>
/// <remarks>
/// <para>
/// One direction only for most types. The broker's vocabulary is richer than the wire's — an
/// <see cref="Exchange"/> carries a version and two thresholds no client has any use for — and
/// translating outward while refusing to translate back keeps it that way: an exchange a client can
/// author is an exchange a client can lie about.
/// </para>
/// <para>
/// <b>Every enum is converted by an explicit arm that throws on anything unrecognised.</b> The obvious
/// alternative is arithmetic, because each proto member is its domain counterpart plus one; that is true
/// today, guaranteed by nothing, and would silently turn a member added in the middle of one enum into a
/// different member of the other.
/// </para>
/// <para>
/// Timestamps go through <see cref="ExchangeRow.FormatTimestamp"/>: the store's representation is the
/// canonical one and the wire carries exactly it.
/// </para>
/// </remarks>
internal static class Conversions
{
    /// <summary>Formats an instant for the wire, or the empty string for none.</summary>
    /// <param name="value">The instant, or <see langword="null"/>.</param>
    /// <returns>The round-trip representation, or an empty string.</returns>
    public static string Stamp(DateTimeOffset? value) =>
        value is { } present ? ExchangeRow.FormatTimestamp(present) : string.Empty;

    /// <summary>Converts a tier for the wire.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not a defined member.</exception>
    public static Wire.AgentTier Out(AgentTier tier) => tier switch
    {
        AgentTier.Full => Wire.AgentTier.Full,
        AgentTier.Pull => Wire.AgentTier.Pull,
        AgentTier.Headless => Wire.AgentTier.Headless,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a defined tier.")
    };

    /// <summary>Reads a tier from the wire.</summary>
    /// <param name="tier">The wire value.</param>
    /// <returns>The tier.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is unspecified or unknown.</exception>
    public static AgentTier In(Wire.AgentTier tier) => tier switch
    {
        Wire.AgentTier.Full => AgentTier.Full,
        Wire.AgentTier.Pull => AgentTier.Pull,
        Wire.AgentTier.Headless => AgentTier.Headless,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a defined tier.")
    };

    /// <summary>Converts a registration status for the wire.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a defined member.</exception>
    public static Wire.AgentStatus Out(AgentStatus status) => status switch
    {
        AgentStatus.Active => Wire.AgentStatus.Active,
        AgentStatus.Retired => Wire.AgentStatus.Retired,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a defined status.")
    };

    /// <summary>Converts a presence for the wire.</summary>
    /// <param name="presence">The presence.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The presence is not a defined member.</exception>
    public static Wire.AgentPresence Out(AgentPresence presence) => presence switch
    {
        AgentPresence.Stranded => Wire.AgentPresence.Stranded,
        AgentPresence.Startable => Wire.AgentPresence.Startable,
        AgentPresence.Running => Wire.AgentPresence.Running,
        AgentPresence.Here => Wire.AgentPresence.Here,
        AgentPresence.Away => Wire.AgentPresence.Away,
        _ => throw new ArgumentOutOfRangeException(nameof(presence), presence, "Not a defined presence.")
    };

    /// <summary>Converts a message type for the wire.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The type is not a defined member.</exception>
    public static Wire.MessageType Out(MessageType type) => type switch
    {
        MessageType.Request => Wire.MessageType.Request,
        MessageType.Clarify => Wire.MessageType.Clarify,
        MessageType.Result => Wire.MessageType.Result,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a defined type.")
    };

    /// <summary>Reads a message type from the wire.</summary>
    /// <param name="type">The wire value.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is unspecified or unknown.</exception>
    public static MessageType In(Wire.MessageType type) => type switch
    {
        Wire.MessageType.Request => MessageType.Request,
        Wire.MessageType.Clarify => MessageType.Clarify,
        Wire.MessageType.Result => MessageType.Result,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a defined type.")
    };

    /// <summary>Converts an exchange state for the wire.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is not a defined member.</exception>
    public static Wire.ExchangeState Out(ExchangeState state) => state switch
    {
        ExchangeState.Open => Wire.ExchangeState.Open,
        ExchangeState.Closed => Wire.ExchangeState.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a defined state.")
    };

    /// <summary>Converts an exchange's rolled-up outcome for the wire.</summary>
    /// <param name="outcome">The outcome, or <see langword="null"/> for an open exchange.</param>
    /// <returns>The wire value, or <c>Unspecified</c> when there is none.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined member.</exception>
    /// <remarks>
    /// One of the few places <c>UNSPECIFIED</c> is a legitimate value rather than a refusal: an open
    /// exchange genuinely has no outcome, and proto3 offers no other way to say so for an enum.
    /// </remarks>
    public static Wire.ExchangeOutcome Out(ExchangeOutcome? outcome) => outcome switch
    {
        null => Wire.ExchangeOutcome.Unspecified,
        ExchangeOutcome.Complete => Wire.ExchangeOutcome.Complete,
        ExchangeOutcome.Blocked => Wire.ExchangeOutcome.Blocked,
        ExchangeOutcome.Closed => Wire.ExchangeOutcome.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a defined outcome.")
    };

    /// <summary>Converts an item outcome for the wire.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined member.</exception>
    public static Wire.Outcome Out(Outcome outcome) => outcome switch
    {
        Outcome.Done => Wire.Outcome.Done,
        Outcome.Answered => Wire.Outcome.Answered,
        Outcome.Acknowledged => Wire.Outcome.Acknowledged,
        Outcome.Blocked => Wire.Outcome.Blocked,
        Outcome.Closed => Wire.Outcome.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a defined outcome.")
    };

    /// <summary>Converts a blocker kind for the wire.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not a defined member.</exception>
    public static Wire.BlockerKind Out(BlockerKind kind) => kind switch
    {
        BlockerKind.MissingInformation => Wire.BlockerKind.MissingInformation,
        BlockerKind.Conflict => Wire.BlockerKind.Conflict,
        BlockerKind.Permission => Wire.BlockerKind.Permission,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined blocker.")
    };

    /// <summary>Converts how a person arrived at an ownership line, for the wire.</summary>
    /// <param name="source">Which of the three routes produced it.</param>
    /// <returns>The wire value.</returns>
    public static Wire.OwnershipSource Out(OwnershipSource source) => source switch
    {
        OwnershipSource.Proposed => Wire.OwnershipSource.Proposed,
        OwnershipSource.Edited => Wire.OwnershipSource.Edited,
        OwnershipSource.Composed => Wire.OwnershipSource.Composed,
        OwnershipSource.Automatic => Wire.OwnershipSource.Automatic,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a defined source.")
    };

    /// <summary>Reads how a person arrived at an ownership line, off the wire.</summary>
    /// <param name="source">The wire value.</param>
    /// <returns>The domain value.</returns>
    /// <remarks>
    /// <c>Unspecified</c> is refused rather than defaulted. It is what an unset field arrives as, and
    /// guessing <see cref="OwnershipSource.Proposed"/> would attribute a line to an agent that may
    /// never have written one — which is the one thing the three values exist to keep apart.
    /// </remarks>
    public static OwnershipSource In(Wire.OwnershipSource source) => source switch
    {
        Wire.OwnershipSource.Proposed => OwnershipSource.Proposed,
        Wire.OwnershipSource.Edited => OwnershipSource.Edited,
        Wire.OwnershipSource.Composed => OwnershipSource.Composed,
        Wire.OwnershipSource.Automatic => OwnershipSource.Automatic,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a defined source.")
    };

    /// <summary>Converts who closed a conversation for the wire.</summary>
    /// <param name="closer">Who closed it, or <see langword="null"/> where nothing recorded it.</param>
    /// <returns>The wire value, or <c>Unspecified</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined member.</exception>
    public static Wire.ConversationCloser Out(ConversationCloser? closer) => closer switch
    {
        null => Wire.ConversationCloser.Unspecified,
        ConversationCloser.Consensus => Wire.ConversationCloser.Consensus,
        ConversationCloser.Person => Wire.ConversationCloser.Person,
        _ => throw new ArgumentOutOfRangeException(nameof(closer), closer, "Not a defined closer.")
    };

    /// <summary>Converts a flag kind for the wire.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not a defined member.</exception>
    public static Wire.FlagKind Out(FlagKind kind) => kind switch
    {
        FlagKind.NotCollected => Wire.FlagKind.NotCollected,
        FlagKind.NoProgress => Wire.FlagKind.NoProgress,
        FlagKind.Retired => Wire.FlagKind.Retired,
        FlagKind.NotVoted => Wire.FlagKind.NotVoted,
        FlagKind.PermissionRefused => Wire.FlagKind.PermissionRefused,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined flag.")
    };

    /// <summary>Converts a closure vote for the wire.</summary>
    /// <param name="vote">The vote.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The vote is not a defined member.</exception>
    public static Wire.Vote Out(Vote vote) => vote switch
    {
        Vote.Done => Wire.Vote.Done,
        Vote.NotDone => Wire.Vote.NotDone,
        _ => throw new ArgumentOutOfRangeException(nameof(vote), vote, "Not a defined vote.")
    };

    /// <summary>Reads a closure vote from the wire.</summary>
    /// <param name="vote">The wire value.</param>
    /// <returns>The vote.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is unspecified or unknown.</exception>
    /// <remarks>
    /// An unset vote is refused rather than read as either answer: a call that said nothing must not be
    /// recorded as a participant having said something.
    /// </remarks>
    public static Vote In(Wire.Vote vote) => vote switch
    {
        Wire.Vote.Done => Vote.Done,
        Wire.Vote.NotDone => Vote.NotDone,
        _ => throw new ArgumentOutOfRangeException(nameof(vote), vote, "Not a defined vote.")
    };

    /// <summary>Converts a Request refusal for the wire.</summary>
    /// <param name="rejection">The refusal.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is not a defined member.</exception>
    public static Wire.RequestRejection Out(RequestRejection rejection) => rejection switch
    {
        RequestRejection.None => Wire.RequestRejection.None,
        RequestRejection.Saturated => Wire.RequestRejection.Saturated,
        RequestRejection.Duplicate => Wire.RequestRejection.Duplicate,
        RequestRejection.ReceiverNotRegistered => Wire.RequestRejection.ReceiverNotRegistered,
        RequestRejection.ReceiverRetired => Wire.RequestRejection.ReceiverRetired,
        RequestRejection.SenderRetired => Wire.RequestRejection.SenderRetired,
        RequestRejection.ConversationClosed => Wire.RequestRejection.ConversationClosed,
        RequestRejection.ConversationNotAssignable => Wire.RequestRejection.ConversationNotAssignable,
        RequestRejection.NoSuchRole => Wire.RequestRejection.NoSuchRole,
        RequestRejection.ReceiverIsTheFacilitator => Wire.RequestRejection.ReceiverIsTheFacilitator,
        _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Not a defined rejection.")
    };

    /// <summary>Converts a purge verdict for the wire.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The verdict is not a defined member.</exception>
    public static Wire.PurgeVerdict Out(PurgeVerdict verdict) => verdict switch
    {
        PurgeVerdict.NotRegistered => Wire.PurgeVerdict.NotRegistered,
        PurgeVerdict.Gone => Wire.PurgeVerdict.Gone,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Not a defined verdict.")
    };

    /// <summary>Writes a destroy's verdict to the wire.</summary>
    /// <param name="verdict">The domain value.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one this build knows.</exception>
    public static Wire.DestroyVerdict Out(DestroyVerdict verdict) => verdict switch
    {
        DestroyVerdict.NoSuchConversation => Wire.DestroyVerdict.NoSuchConversation,
        DestroyVerdict.Gone => Wire.DestroyVerdict.Gone,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Not a defined verdict.")
    };

    /// <summary>Reads a destroy's verdict from the wire.</summary>
    /// <param name="verdict">The wire value.</param>
    /// <returns>The domain value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one this build knows.</exception>
    public static DestroyVerdict In(Wire.DestroyVerdict verdict) => verdict switch
    {
        Wire.DestroyVerdict.NoSuchConversation => DestroyVerdict.NoSuchConversation,
        Wire.DestroyVerdict.Gone => DestroyVerdict.Gone,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Not a defined verdict.")
    };

    /// <summary>Reads an instruction's outcome from the wire.</summary>
    /// <param name="outcome">The wire value.</param>
    /// <returns>The domain value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is unspecified or unknown.</exception>
    /// <remarks>
    /// A node reporting a value this build has never heard of is a node from a different version, and
    /// reading that as failed would tell a person their agent broke when the deployment did.
    /// </remarks>
    public static InstructionOutcome In(Wire.InstructionOutcome outcome) => outcome switch
    {
        Wire.InstructionOutcome.Pending => InstructionOutcome.Pending,
        Wire.InstructionOutcome.Taken => InstructionOutcome.Taken,
        Wire.InstructionOutcome.Declined => InstructionOutcome.Declined,
        Wire.InstructionOutcome.Undeliverable => InstructionOutcome.Undeliverable,
        Wire.InstructionOutcome.Failed => InstructionOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a defined outcome.")
    };

    /// <summary>Converts an instruction's outcome for the wire.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined member.</exception>
    public static Wire.InstructionOutcome Out(InstructionOutcome outcome) => outcome switch
    {
        InstructionOutcome.Pending => Wire.InstructionOutcome.Pending,
        InstructionOutcome.Taken => Wire.InstructionOutcome.Taken,
        InstructionOutcome.Declined => Wire.InstructionOutcome.Declined,
        InstructionOutcome.Undeliverable => Wire.InstructionOutcome.Undeliverable,
        InstructionOutcome.Failed => Wire.InstructionOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a defined outcome.")
    };

    /// <summary>Converts a refused move for the wire.</summary>
    /// <param name="error">The refusal, or <see langword="null"/> when the move succeeded.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is not a defined member.</exception>
    public static Wire.TransitionError Out(TransitionError? error) => error switch
    {
        null or TransitionError.None => Wire.TransitionError.None,
        TransitionError.ExchangeNotOpen => Wire.TransitionError.ExchangeNotOpen,
        TransitionError.NotAParticipant => Wire.TransitionError.NotAParticipant,
        TransitionError.NotYourTurn => Wire.TransitionError.NotYourTurn,
        TransitionError.OnlyReceiverSendsResult => Wire.TransitionError.OnlyReceiverSendsResult,
        TransitionError.ClarifyDepthExceeded => Wire.TransitionError.ClarifyDepthExceeded,
        TransitionError.OutcomesDoNotMatchItems => Wire.TransitionError.OutcomesDoNotMatchItems,
        TransitionError.ClosedIsTheBrokers => Wire.TransitionError.ClosedIsTheBrokers,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Not a defined error.")
    };

    /// <summary>Converts what became of a receiver for the wire.</summary>
    /// <param name="outcome">What became of it.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined member.</exception>
    public static Wire.WakeOutcome Out(Core.WakeOutcome outcome) => outcome switch
    {
        Core.WakeOutcome.Started => Wire.WakeOutcome.Started,
        Core.WakeOutcome.AlreadyRunning => Wire.WakeOutcome.AlreadyRunning,
        Core.WakeOutcome.AlreadyComing => Wire.WakeOutcome.AlreadyComing,
        Core.WakeOutcome.Unreachable => Wire.WakeOutcome.Unreachable,
        Core.WakeOutcome.WithThePerson => Wire.WakeOutcome.WithThePerson,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a defined wake outcome.")
    };

    /// <summary>Converts a refused conclusion for the wire.</summary>
    /// <param name="error">The refusal.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is not a defined member.</exception>
    public static Wire.ClosureError Out(Parley.Broker.ClosureError error) => error switch
    {
        Parley.Broker.ClosureError.None => Wire.ClosureError.None,
        Parley.Broker.ClosureError.NotAParticipant => Wire.ClosureError.NotAParticipant,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Not a defined error.")
    };

    /// <summary>Converts what became of one Request for the wire.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <param name="wake">What became of the receiver, when the Request was admitted.</param>
    /// <returns>The wire outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is null.</exception>
    public static Wire.RequestOutcome Out(Parley.Broker.RequestOutcome outcome, Core.WakeOutcome? wake)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var reply = new Wire.RequestOutcome
        {
            Ok = outcome.Succeeded,
            Rejection = Out(outcome.Rejection),
            ExchangeId = outcome.Exchange?.Id ?? 0,
            Receiver = outcome.Exchange?.Receiver.Value ?? string.Empty,
            RequestGroup = outcome.RequestGroup ?? string.Empty,
            ConversationId = outcome.Request?.ConversationId ?? string.Empty,
            ClosedBy = Out(outcome.Admission.ClosedBy)
        };

        reply.OpenExchanges.AddRange(outcome.Admission.Outstanding.Select(e => e.Id));
        reply.AssignableConversations.AddRange(outcome.Admission.Assignable);
        reply.Cycle.AddRange(outcome.Admission.CyclePath.Select(a => a.Value));

        if (wake is { } woken)
        {
            reply.Wake = Out(woken);
        }

        return reply;
    }

    /// <summary>Converts a stored message for the wire.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The wire message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static Wire.Message Out(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new Wire.Message
        {
            Id = message.Id,
            ExchangeId = message.ExchangeId,
            Type = Out(message.Type),
            From = message.From.Value,
            To = message.To.Value,
            ResponderModel = message.ResponderModel,
            PayloadJson = message.PayloadJson,
            ConversationId = message.ConversationId,
            CreatedUtc = Stamp(message.CreatedUtc),
            DeliveredUtc = Stamp(message.DeliveredUtc)
        };
    }

    /// <summary>Converts a message and its envelope for the wire.</summary>
    /// <param name="delivered">The message as the inbox produced it.</param>
    /// <returns>The wire message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="delivered"/> is null.</exception>
    public static Wire.DeliveredMessage Out(DeliveredMessage delivered)
    {
        ArgumentNullException.ThrowIfNull(delivered);

        return new Wire.DeliveredMessage { Message = Out(delivered.Message), Envelope = delivered.Envelope };
    }

    /// <summary>Converts a closure poll for the wire.</summary>
    /// <param name="poll">The poll.</param>
    /// <returns>The wire poll.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="poll"/> is null.</exception>
    public static Wire.ClosurePoll Out(ClosurePoll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);

        return new Wire.ClosurePoll { ConversationId = poll.ConversationId, PolledUtc = Stamp(poll.PolledUtc) };
    }

    /// <summary>Converts an agent summary for the wire.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The wire summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> is null.</exception>
    public static Wire.AgentSummary Out(AgentSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new Wire.AgentSummary
        {
            Name = summary.Name.Value,
            Adapter = summary.Adapter,
            Tier = Out(summary.Tier),
            Presence = Out(summary.Presence),
            Owns = summary.Owns ?? string.Empty
        };
    }

    /// <summary>Converts one of Parley's questions for the wire (§10.2 <c>facilitation</c>).</summary>
    /// <param name="question">The question.</param>
    /// <returns>The wire question.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is null.</exception>
    public static Wire.OwnershipQuestion Out(OwnershipQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return new Wire.OwnershipQuestion
        {
            Agent = question.Agent.Value,
            ConversationId = question.Conversation,
            ExchangeId = question.Exchange,
            State = question.State switch
            {
                QuestionState.Asked => Wire.QuestionState.Asked,
                QuestionState.AskedBack => Wire.QuestionState.AskedBack,
                QuestionState.Answered => Wire.QuestionState.Answered,
                QuestionState.Waiting => Wire.QuestionState.Waiting,
                QuestionState.Accepted => Wire.QuestionState.Accepted,
                QuestionState.Discarded => Wire.QuestionState.Discarded,
                _ => throw new ArgumentOutOfRangeException(nameof(question), question.State, "Not a defined state.")
            },
            Answer = question.Answer ?? string.Empty,
            Flattened = question.Flattened ?? string.Empty,
            WaitingBecause = question.WaitingBecause ?? string.Empty,
            AskedUtc = question.AskedUtc.ToString("O", CultureInfo.InvariantCulture),
            SettledUtc = question.SettledUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    /// <summary>Converts a registry row for the wire.</summary>
    /// <param name="record">The registration.</param>
    /// <returns>The wire row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    /// <remarks>
    /// This one carries the workspace and the session handle, which <see cref="Out(AgentSummary)"/>
    /// deliberately does not: it is reached through the operator service only.
    /// </remarks>
    public static Wire.RegistryRow Out(AgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new Wire.RegistryRow
        {
            Name = record.Name.Value,
            Adapter = record.Adapter,
            Tier = Out(record.Tier),
            Status = Out(record.Status),
            Presence = Out(record.Presence),
            Workspace = record.Workspace ?? string.Empty,
            SessionRef = record.SessionRef ?? string.Empty,
            Owns = record.Owns ?? string.Empty,
            Connected = record.Connected,

            // **Both halves travel rather than a `pending` flag.** A surface says the line is pending
            // by comparing what a person accepted with what peers read, so it can also show the two
            // side by side and say which is which -- a flag would carry the comparison and lose the
            // thing being compared.
            OwnsAccepted = record.OwnsAccepted ?? string.Empty,
            OwnsSource = record.OwnsSource is { } source ? Out(source) : Wire.OwnershipSource.Unspecified,

            // The turn standing (SPEC.md §11.6). Absent means nobody read it, which travels as unreported.
            TurnsReported = record.Turns?.Reported ?? false,
            TurnRunningSinceUtc = record.Turns?.RunningSince is { } running ? Stamp(running) : string.Empty,
            LastTurnEndedUtc = record.Turns?.LastEnded is { } ended ? Stamp(ended) : string.Empty,
            LastTurnClean = record.Turns?.LastClean ?? false,
            LastTurnPermissionDenials = record.Turns?.LastPermissionDenials ?? 0,
        };
    }

    /// <summary>Converts a file operation's refusal reason for the wire.</summary>
    /// <param name="rejection">The reason.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reason is not a defined member.</exception>
    public static Wire.FileRejection Out(FileRejection rejection) => rejection switch
    {
        FileRejection.None => Wire.FileRejection.None,
        FileRejection.BadName => Wire.FileRejection.BadName,
        FileRejection.NamePlacedByAnother => Wire.FileRejection.NamePlacedByAnother,
        FileRejection.NoSuchFile => Wire.FileRejection.NoSuchFile,
        FileRejection.AmbiguousName => Wire.FileRejection.AmbiguousName,
        FileRejection.ConversationNotAssignable => Wire.FileRejection.ConversationNotAssignable,
        FileRejection.ConversationClosed => Wire.FileRejection.ConversationClosed,
        FileRejection.Changed => Wire.FileRejection.Changed,
        FileRejection.Incomplete => Wire.FileRejection.Incomplete,
        FileRejection.NoSpace => Wire.FileRejection.NoSpace,
        FileRejection.ExtensionNotAllowed => Wire.FileRejection.ExtensionNotAllowed,
        FileRejection.NotFetched => Wire.FileRejection.NotFetched,
        FileRejection.TooManyNames => Wire.FileRejection.TooManyNames,
        FileRejection.SafeNameTaken => Wire.FileRejection.SafeNameTaken,
        _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Not a defined file rejection.")
    };

    /// <summary>Converts what a placement came to for the wire.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined member.</exception>
    public static Wire.PlaceOutcome Out(PlaceOutcome outcome) => outcome switch
    {
        PlaceOutcome.Placed => Wire.PlaceOutcome.Placed,
        PlaceOutcome.Replaced => Wire.PlaceOutcome.Replaced,
        PlaceOutcome.AlreadyCurrent => Wire.PlaceOutcome.AlreadyCurrent,
        PlaceOutcome.PlacedByAnother => Wire.PlaceOutcome.PlacedByAnother,
        PlaceOutcome.DoesNotFit => Wire.PlaceOutcome.DoesNotFit,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a defined place outcome.")
    };

    /// <summary>Converts a shared file for the wire: what it is, never what is in it.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The wire view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static Wire.SharedFileView Out(SharedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new Wire.SharedFileView
        {
            ConversationId = file.ConversationId,
            Name = file.Name.Value,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256.Value,
            PlacedBy = file.PlacedBy?.Value ?? string.Empty,
            PlacedUtc = Stamp(file.PlacedUtc),
            ChangedUtc = Stamp(file.ChangedUtc),
            Folder = file.Folder
        };
    }

    /// <summary>Converts a page of the file location for the wire.</summary>
    /// <param name="page">The page.</param>
    /// <param name="placements">Placement counts, for the Operators service only; <see langword="null"/> for an agent.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
    /// <remarks>
    /// Placement counts are the person's evidence for a size limit and are not an agent's business, so an agent's
    /// reply leaves the field empty rather than carrying them.
    /// </remarks>
    public static Wire.FilesReply Out(SharedFilePage page, IReadOnlyList<FilePlacementCount>? placements)
    {
        ArgumentNullException.ThrowIfNull(page);

        var reply = new Wire.FilesReply
        {
            TotalBytes = page.TotalBytes,
            TotalFiles = page.TotalFiles,
            NextPageToken = page.NextPageToken ?? string.Empty
        };

        reply.Files.AddRange(page.Files.Select(Out));
        reply.Totals.AddRange(page.Totals.Select(t => new Wire.ConversationFilesTotal
        {
            ConversationId = t.ConversationId,
            Files = t.Files,
            Bytes = t.Bytes
        }));

        if (placements is not null)
        {
            reply.Placements.AddRange(placements.Select(p => new Wire.FilePlacementCount
            {
                Agent = p.Agent,
                Placements = p.Placements,
                Bytes = p.Bytes,
                LastUtc = Stamp(p.LastUtc)
            }));
        }

        return reply;
    }

    /// <summary>Converts a change notice for the wire.</summary>
    /// <param name="notice">The notice.</param>
    /// <returns>The wire notice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="notice"/> is null.</exception>
    public static Wire.FileNotice Out(FileNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return new Wire.FileNotice
        {
            ConversationId = notice.ConversationId,
            Name = notice.Name,
            Sha256Had = notice.Had.Value,
            Sha256Now = notice.Now?.Value ?? string.Empty,
            SizeBytes = notice.SizeBytes ?? 0,
            ChangedUtc = Stamp(notice.ChangedUtc),
            Gone = notice.Gone
        };
    }

    /// <summary>Converts a file reference for the wire.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The wire reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
    public static Wire.FileRef Out(FileRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return new Wire.FileRef { ConversationId = reference.ConversationId ?? string.Empty, Name = reference.Name };
    }

    /// <summary>Reads a file reference from the wire.</summary>
    /// <param name="reference">The wire reference.</param>
    /// <returns>The reference; an empty conversation means one was not named.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
    public static FileRef In(Wire.FileRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return new FileRef(string.IsNullOrEmpty(reference.ConversationId) ? null : reference.ConversationId, reference.Name);
    }

    /// <summary>Converts a refusal of one file for the wire.</summary>
    /// <param name="refusal">The refusal.</param>
    /// <param name="detail">The rule in words, when there is one to say.</param>
    /// <param name="sha256Now">The hash the location holds, on a refusal for changed content.</param>
    /// <returns>The wire refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="refusal"/> is null.</exception>
    public static Wire.FileRefusal Out(FileRefusal refusal, string? detail = null, ContentHash? sha256Now = null)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        var wire = new Wire.FileRefusal
        {
            File = Out(refusal.File),
            Rejection = Out(refusal.Rejection),
            PlacedBy = refusal.PlacedBy ?? string.Empty,
            ConversationsMore = refusal.ConversationsMore,
            Sha256Now = sha256Now?.Value ?? string.Empty,
            Detail = detail ?? string.Empty
        };

        wire.Conversations.AddRange(refusal.Conversations);

        return wire;
    }
}
