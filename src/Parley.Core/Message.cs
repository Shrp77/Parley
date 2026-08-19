namespace Parley.Core;

/// <summary>
/// One message in the log, as stored and as delivered.
/// </summary>
/// <param name="Id">The storage identity assigned when the message was appended.</param>
/// <param name="ExchangeId">
/// The exchange this message belongs to, or <see langword="null"/> for a
/// <see cref="MessageType.Notify"/>, which opens no exchange and claims no pair slot.
/// </param>
/// <param name="Type">The kind of message.</param>
/// <param name="From">The sending agent.</param>
/// <param name="To">The receiving agent.</param>
/// <param name="ResponderModel">
/// What produced this message. See SPEC.md §11.3 — required on every message, not only
/// answers, and rendered into the trust envelope.
/// </param>
/// <param name="PayloadJson">The payload document, capped at 2,000 characters by the schema.</param>
/// <param name="SessionTag">
/// The operator tag in force when this was appended, or <see langword="null"/> for ordinary
/// untagged traffic. Set by the operator, never by an agent. See SPEC.md §9.4.
/// </param>
/// <param name="CreatedUtc">When the message was appended.</param>
/// <param name="DeliveredUtc">
/// When the message was handed to the recipient, or <see langword="null"/> while it is still
/// pending in their inbox.
/// </param>
/// <remarks>
/// <para>
/// A record rather than an aggregate, deliberately. <see cref="Exchange"/> guards transitions
/// because an exchange has a lifecycle; a message has none. Once appended it is a fact, and
/// the only thing that changes about it is whether it has been delivered yet.
/// </para>
/// <para>
/// The trust envelope is not a field here. It is rendered at delivery from registry state and
/// never stored, so that correcting its wording protects every message already sent, and so
/// that its per-delivery token stays out of the deduplication hash — which it would otherwise
/// defeat by making every message unique.
/// </para>
/// </remarks>
public sealed record Message(
    long Id,
    long? ExchangeId,
    MessageType Type,
    AgentName From,
    AgentName To,
    string ResponderModel,
    string PayloadJson,
    string? SessionTag,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? DeliveredUtc)
{
    /// <summary>
    /// Gets a value indicating whether the message is still awaiting delivery.
    /// </summary>
    /// <value>
    /// <see langword="true"/> while <see cref="DeliveredUtc"/> is unset. This is the condition
    /// the inbox index is built on.
    /// </value>
    public bool IsPending => DeliveredUtc is null;

    /// <summary>
    /// Gets a value indicating whether this message carries an operator session tag.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="SessionTag"/> is set.
    /// </value>
    /// <remarks>
    /// Named for what it observes rather than what follows from it. SPEC.md §9.4 makes a tag
    /// the thing that exempts a message from pruning, but no retention sweep exists yet, so a
    /// property called <c>IsRetained</c> would promise a guarantee nothing currently provides.
    /// </remarks>
    public bool IsTagged => SessionTag is not null;
}
