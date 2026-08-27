using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Mcp;

/// <summary>
/// The two enums this adapter has to translate, and no others.
/// </summary>
/// <remarks>
/// <para>
/// The broker keeps a much larger conversion table, and this is deliberately not a client-side
/// copy of it. Almost everything the adapter receives it formats directly from the wire — a
/// refusal reaches a model as a sentence, and the <c>switch</c> that chooses the sentence can
/// switch on the wire value as easily as on a domain one. Only two things genuinely need
/// converting, and each for its own reason.
/// </para>
/// <para>
/// <see cref="Out(MessageType)"/> exists because the size cap is checked here, before anything
/// goes on the wire, and reporting the refusal means naming the kind of message that was
/// refused. <see cref="Out(Core.Resolution)"/> exists because an answer's rollup is computed here
/// from the per-sub-question resolutions the caller supplied, which is the adapter's own
/// arithmetic over §5.3.
/// </para>
/// <para>
/// Both directions of every one of these pairs are checked by <c>TransportEnumTests</c> against
/// the domain enum, by reflection. That suite exists because the wire's copies drifted from the
/// domain's while they were being written — four of five wrong at one point — and a table like
/// this one is exactly where that goes unnoticed.
/// </para>
/// </remarks>
internal static class WireCodec
{
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
    /// <remarks>
    /// For the inbox, which names each message's kind to the model using
    /// <see cref="MessageIdentity.FormatType"/> — the same spelling the identity hash is computed
    /// over, so the word an agent reads is the word the protocol uses.
    /// </remarks>
    public static MessageType In(Wire.MessageType type) => type switch
    {
        Wire.MessageType.Question => MessageType.Question,
        Wire.MessageType.Clarify => MessageType.Clarify,
        Wire.MessageType.Answer => MessageType.Answer,
        Wire.MessageType.Notify => MessageType.Notify,
        Wire.MessageType.Ghost => MessageType.Ghost,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a defined type.")
    };

    /// <summary>Converts an answer's rollup for the wire.</summary>
    /// <param name="resolution">The rollup.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is not a defined member.</exception>
    /// <remarks>
    /// Qualified, because <c>ParleyTools</c> declares a <c>Resolution</c> of its own for the
    /// per-sub-question shape a caller supplies. The two are genuinely different things — one is
    /// an outcome, the other is a record carrying an identifier and a confidence — and the
    /// collision is worth living with rather than renaming a name that appears in a tool schema
    /// every model reads.
    /// </remarks>
    public static Wire.Resolution Out(Core.Resolution resolution) => resolution switch
    {
        Core.Resolution.Answered => Wire.Resolution.Answered,
        Core.Resolution.OutOfScope => Wire.Resolution.OutOfScope,
        Core.Resolution.Unknown => Wire.Resolution.Unknown,
        Core.Resolution.Ghosted => Wire.Resolution.Ghosted,
        _ => throw new ArgumentOutOfRangeException(
            nameof(resolution), resolution, "Not a defined resolution.")
    };
}
