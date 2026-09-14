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
            Connected = record.Connected
        };
    }
}
