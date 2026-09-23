namespace Parley.Core;

/// <summary>
/// One message in the log, as stored and as delivered.
/// </summary>
/// <param name="Id">The storage identity assigned when the message was appended.</param>
/// <param name="ExchangeId">The exchange this message belongs to. Every message belongs to one (§5).</param>
/// <param name="Type">The kind of message.</param>
/// <param name="From">The sender.</param>
/// <param name="To">The recipient.</param>
/// <param name="ResponderModel">What produced this message (§11.3), rendered into the envelope.</param>
/// <param name="PayloadJson">The payload document, capped by the schema; see <see cref="MessageLimits"/>.</param>
/// <param name="ConversationId">
/// The conversation the exchange belongs to, which the broker assigned (§5.0) and nobody chose here.
/// </param>
/// <param name="CreatedUtc">When the message was appended.</param>
/// <param name="DeliveredUtc">When it was handed to the recipient, or <see langword="null"/> while pending.</param>
/// <remarks>
/// <para>
/// A record rather than an aggregate. <see cref="Exchange"/> guards transitions because an exchange has
/// a lifecycle; a message has none. Once appended it is a fact, and the only thing that changes about
/// it is whether it has been delivered.
/// </para>
/// <para>
/// The envelope is not a field here. It is rendered at delivery from registry state and never stored,
/// so correcting its wording protects every message already sent.
/// </para>
/// </remarks>
public sealed record Message(
    long Id,
    long ExchangeId,
    MessageType Type,
    AgentName From,
    AgentName To,
    string ResponderModel,
    string PayloadJson,
    string ConversationId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? DeliveredUtc)
{
    /// <summary>Gets a value indicating whether the message is still awaiting delivery.</summary>
    /// <value><see langword="true"/> while <see cref="DeliveredUtc"/> is unset.</value>
    public bool IsPending => DeliveredUtc is null;
}
