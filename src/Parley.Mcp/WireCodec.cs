using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Mcp;

/// <summary>
/// The one enum this adapter has to translate, and no other.
/// </summary>
/// <remarks>
/// <para>
/// The broker keeps a much larger conversion table, and this is deliberately not a client-side copy of
/// it. Almost everything the adapter receives it formats directly from the wire: a refusal reaches a model
/// as a sentence, and the <c>switch</c> that chooses the sentence can switch on the wire value.
/// </para>
/// <para>
/// The message type is the exception, in both directions: the size cap is checked here before anything
/// goes on the wire and the refusal names the kind of message, and the inbox names each message's kind to
/// the model in the protocol's own word. <c>TransportEnumTests</c> checks both against the domain enum.
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
}
