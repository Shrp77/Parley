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
/// <see cref="Exchange"/> carries a version and three windows that no client has any use for —
/// and translating outward while refusing to translate back keeps it that way. A round trip
/// would invite a client to send an exchange, and an exchange a client can author is an
/// exchange a client can lie about.
/// </para>
/// <para>
/// <b>Every enum is converted by an explicit arm that throws on anything unrecognised.</b> The
/// obvious alternative is arithmetic: each proto member is its domain counterpart plus one,
/// because of the zero-valued <c>UNSPECIFIED</c> proto3 requires. That relationship is true
/// today, is not guaranteed by anything, and would convert a member added in the middle of one
/// enum into a different member of the other — silently, and in whichever direction was less
/// tested. The switches are longer and they fail loudly, which is the trade being made.
/// </para>
/// <para>
/// Timestamps go through <see cref="ExchangeRow.FormatTimestamp"/> rather than a format string
/// declared here. The store's representation is the canonical one and the wire carries exactly
/// it; a second formatter would be a second definition of the same thing, agreeing until one of
/// them was changed.
/// </para>
/// </remarks>
internal static class Conversions
{
    /// <summary>Formats an instant for the wire, or the empty string for none.</summary>
    /// <param name="value">The instant, or <see langword="null"/>.</param>
    /// <returns>The round-trip representation, or an empty string.</returns>
    /// <remarks>
    /// Absence is the empty string because proto3 has no null for a scalar. That is a real loss
    /// of fidelity for a string that could legitimately be empty, and a timestamp cannot be:
    /// there is no instant whose round-trip form has no characters in it, so the encoding is
    /// unambiguous here even though it would not be everywhere.
    /// </remarks>
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
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is unspecified or unknown, which is what a client built against a newer
    /// contract sends and what a client that set nothing at all sends.
    /// </exception>
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
        _ => throw new ArgumentOutOfRangeException(
            nameof(presence), presence, "Not a defined presence.")
    };

    /// <summary>Converts a message type for the wire.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The type is not a defined member.</exception>
    public static Wire.MessageType Out(MessageType type) => type switch
    {
        MessageType.Question => Wire.MessageType.Question,
        MessageType.Clarify => Wire.MessageType.Clarify,
        MessageType.Answer => Wire.MessageType.Answer,
        MessageType.Notify => Wire.MessageType.Notify,
        MessageType.Ghost => Wire.MessageType.Ghost,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a defined type.")
    };

    /// <summary>Reads a message type from the wire.</summary>
    /// <param name="type">The wire value.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is unspecified or unknown.</exception>
    public static MessageType In(Wire.MessageType type) => type switch
    {
        Wire.MessageType.Question => MessageType.Question,
        Wire.MessageType.Clarify => MessageType.Clarify,
        Wire.MessageType.Answer => MessageType.Answer,
        Wire.MessageType.Notify => MessageType.Notify,
        Wire.MessageType.Ghost => MessageType.Ghost,
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
        ExchangeState.Orphaned => Wire.ExchangeState.Orphaned,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a defined state.")
    };

    /// <summary>Converts a resolution for the wire.</summary>
    /// <param name="resolution">The resolution, or <see langword="null"/> for an open exchange.</param>
    /// <returns>The wire value, or <c>Unspecified</c> when there is none.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is not a defined member.</exception>
    /// <remarks>
    /// This is the one place <c>UNSPECIFIED</c> is a legitimate value rather than a refusal: an
    /// open exchange genuinely has no resolution, and proto3 offers no other way to say so for
    /// an enum field.
    /// </remarks>
    public static Wire.Resolution Out(Resolution? resolution) => resolution switch
    {
        null => Wire.Resolution.Unspecified,
        Resolution.Answered => Wire.Resolution.Answered,
        Resolution.OutOfScope => Wire.Resolution.OutOfScope,
        Resolution.Unknown => Wire.Resolution.Unknown,
        Resolution.Ghosted => Wire.Resolution.Ghosted,
        _ => throw new ArgumentOutOfRangeException(
            nameof(resolution), resolution, "Not a defined resolution.")
    };

    /// <summary>Reads a resolution from the wire.</summary>
    /// <param name="resolution">The wire value.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is unspecified or unknown.</exception>
    /// <remarks>
    /// Unspecified is refused rather than defaulted to <see cref="Resolution.Answered"/>. That
    /// default would be a claim on the answerer's behalf that it had answered — the most
    /// consequential of the four, and the one an unset field is least likely to have meant.
    /// </remarks>
    public static Resolution In(Wire.Resolution resolution) => resolution switch
    {
        Wire.Resolution.Answered => Resolution.Answered,
        Wire.Resolution.OutOfScope => Resolution.OutOfScope,
        Wire.Resolution.Unknown => Resolution.Unknown,
        Wire.Resolution.Ghosted => Resolution.Ghosted,
        _ => throw new ArgumentOutOfRangeException(
            nameof(resolution), resolution, "Not a defined resolution.")
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
    /// An unset vote is refused rather than read as an abstention. SPEC.md §8 has silence lapse
    /// into consent on its own schedule, so a call that said nothing would conclude a
    /// conversation without anybody having decided to — and the caller would have no way to know
    /// it had, because the field it failed to set is the one that says so.
    /// </remarks>
    public static Vote In(Wire.Vote vote) => vote switch
    {
        Wire.Vote.Done => Vote.Done,
        Wire.Vote.NotDone => Vote.NotDone,
        _ => throw new ArgumentOutOfRangeException(nameof(vote), vote, "Not a defined vote.")
    };

    /// <summary>Converts an admission refusal for the wire.</summary>
    /// <param name="rejection">The refusal.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is not a defined member.</exception>
    public static Wire.OpenRejection Out(OpenRejection rejection) => rejection switch
    {
        OpenRejection.None => Wire.OpenRejection.None,
        OpenRejection.PairSaturated => Wire.OpenRejection.PairSaturated,
        OpenRejection.DuplicateQuestion => Wire.OpenRejection.DuplicateQuestion,
        OpenRejection.WouldDeadlock => Wire.OpenRejection.WouldDeadlock,
        OpenRejection.AnswererUnreachable => Wire.OpenRejection.AnswererUnreachable,
        OpenRejection.ConversationClosed => Wire.OpenRejection.ConversationClosed,
        OpenRejection.AskerRetired => Wire.OpenRejection.AskerRetired,
        _ => throw new ArgumentOutOfRangeException(
            nameof(rejection), rejection, "Not a defined rejection.")
    };

    /// <summary>Converts a purge verdict for the wire.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The verdict is not a defined member.</exception>
    public static Wire.PurgeVerdict Out(PurgeVerdict verdict) => verdict switch
    {
        PurgeVerdict.NotRegistered => Wire.PurgeVerdict.NotRegistered,
        PurgeVerdict.Gone => Wire.PurgeVerdict.Gone,
        _ => throw new ArgumentOutOfRangeException(
            nameof(verdict), verdict, "Not a defined verdict.")
    };

    /// <summary>Reads an instruction's outcome from the wire.</summary>
    /// <param name="outcome">The wire value.</param>
    /// <returns>The domain value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is unspecified or unknown.</exception>
    /// <remarks>
    /// Unspecified is refused rather than mapped. A node reporting a value this build has never
    /// heard of is a node from a different version, and quietly reading that as `failed` would
    /// tell an operator their agent broke when what broke was the deployment.
    /// </remarks>
    public static InstructionOutcome In(Wire.InstructionOutcome outcome) => outcome switch
    {
        Wire.InstructionOutcome.Pending => InstructionOutcome.Pending,
        Wire.InstructionOutcome.Taken => InstructionOutcome.Taken,
        Wire.InstructionOutcome.Declined => InstructionOutcome.Declined,
        Wire.InstructionOutcome.Undeliverable => InstructionOutcome.Undeliverable,
        Wire.InstructionOutcome.Failed => InstructionOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "Not a defined outcome.")
    };

    /// <summary>Converts an instruction's outcome for the wire.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined member.</exception>
    /// <remarks>
    /// Every member is listed rather than cast, like every other conversion here. The four
    /// outcomes of SPEC.md §10.2 are four different facts and a cast would make them one
    /// arithmetic accident away from each other — with `declined` arriving as `failed` being the
    /// one that matters, because §11.5 says a refusal is an agent behaving correctly.
    /// </remarks>
    public static Wire.InstructionOutcome Out(InstructionOutcome outcome) => outcome switch
    {
        InstructionOutcome.Pending => Wire.InstructionOutcome.Pending,
        InstructionOutcome.Taken => Wire.InstructionOutcome.Taken,
        InstructionOutcome.Declined => Wire.InstructionOutcome.Declined,
        InstructionOutcome.Undeliverable => Wire.InstructionOutcome.Undeliverable,
        InstructionOutcome.Failed => Wire.InstructionOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "Not a defined outcome.")
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
        TransitionError.OnlyAnswererMayAnswer => Wire.TransitionError.OnlyAnswererMayAnswer,
        TransitionError.ClarifyDepthExceeded => Wire.TransitionError.ClarifyDepthExceeded,
        TransitionError.ExchangeNotOrphaned => Wire.TransitionError.ExchangeNotOrphaned,
        TransitionError.GhostIsNotAnAnswer => Wire.TransitionError.GhostIsNotAnAnswer,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Not a defined error.")
    };

    /// <summary>Converts a refused notification for the wire.</summary>
    /// <param name="rejection">The refusal.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is not a defined member.</exception>
    public static Wire.NotifyRejection Out(Parley.Broker.NotifyRejection rejection) => rejection switch
    {
        Parley.Broker.NotifyRejection.None => Wire.NotifyRejection.None,
        Parley.Broker.NotifyRejection.RecipientUnroutable => Wire.NotifyRejection.RecipientUnroutable,
        Parley.Broker.NotifyRejection.SenderRetired => Wire.NotifyRejection.SenderRetired,
        Parley.Broker.NotifyRejection.ConversationClosed => Wire.NotifyRejection.ConversationClosed,
        _ => throw new ArgumentOutOfRangeException(
            nameof(rejection), rejection, "Not a defined rejection.")
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

            // Zero for none. An identifier is issued by SQLite's rowid, which starts at one, so
            // no real exchange can collide with the sentinel.
            ExchangeId = message.ExchangeId ?? 0,
            Type = Out(message.Type),
            From = message.From.Value,
            To = message.To.Value,
            ResponderModel = message.ResponderModel,
            PayloadJson = message.PayloadJson,
            ConversationId = message.ConversationId ?? string.Empty,
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

        return new Wire.DeliveredMessage
        {
            Message = Out(delivered.Message),
            Envelope = delivered.Envelope
        };
    }

    /// <summary>Converts a closure poll for the wire.</summary>
    /// <param name="poll">The poll.</param>
    /// <returns>The wire poll.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="poll"/> is null.</exception>
    public static Wire.ClosurePoll Out(ClosurePoll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);

        return new Wire.ClosurePoll
        {
            ConversationId = poll.ConversationId,
            PolledUtc = Stamp(poll.PolledUtc),
            ExpiresUtc = Stamp(poll.ExpiresUtc)
        };
    }

    /// <summary>Converts an exchange for the wire.</summary>
    /// <param name="exchange">The exchange.</param>
    /// <returns>The wire view of it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> is null.</exception>
    /// <remarks>
    /// A view rather than the exchange. The three windows and the optimistic-concurrency version
    /// are the broker's own business: a client cannot act on them, and publishing them would
    /// make them part of the contract and so a thing that could not be changed without a wire
    /// revision.
    /// </remarks>
    public static Wire.ExchangeView Out(Exchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        return new Wire.ExchangeView
        {
            Id = exchange.Id,
            Asker = exchange.Asker.Value,
            Answerer = exchange.Answerer.Value,
            OwedBy = exchange.OwedBy.Value,
            State = Out(exchange.State),
            ClarifyDepth = exchange.ClarifyDepth,
            ClarifyDepthCap = exchange.ClarifyDepthCap,
            OpenedUtc = Stamp(exchange.OpenedUtc),
            ExpiresUtc = Stamp(exchange.ExpiresUtc),
            ClosedUtc = Stamp(exchange.ClosedUtc),
            Resolution = Out(exchange.Resolution),
            IsDecision = exchange.IsDecision
        };
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
    /// This one carries the workspace and the session handle, which
    /// <see cref="Out(AgentSummary)"/> deliberately does not. It is reached through the operator
    /// service, and SPEC.md §10.2 is where an operator is allowed to see across the deployment;
    /// §10.1 is where an agent is not.
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
