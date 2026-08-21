namespace Parley.Core;

/// <summary>
/// A message and the envelope it must be delivered inside, per SPEC.md §13.
/// </summary>
/// <param name="Message">The message as stored.</param>
/// <param name="Envelope">
/// The text to place in the receiving agent's context. Contains the payload; the payload is
/// never placed anywhere else.
/// </param>
/// <remarks>
/// <para>
/// The pairing exists so that an adapter cannot deliver a peer's words without the framing
/// around them. §13 says the broker wraps and never the sender, and that was true of the code
/// only in the sense that <see cref="TrustEnvelope.Wrap"/> existed — nothing called it, and the
/// inbox handed back raw messages for an adapter to render however it liked. An adapter that
/// forgot, or rendered a field of the payload directly, would have turned Parley into the
/// prompt-injection channel §13 opens by describing.
/// </para>
/// <para>
/// The envelope is produced at delivery and is not stored. §13 requires that: it is derived
/// from authoritative state each time, so correcting the wording protects every message ever
/// sent rather than only future ones. It also carries a token that is fresh per delivery, so
/// two deliveries of the same message do not have the same envelope, and one of them is not
/// evidence for forging the other.
/// </para>
/// <para>
/// <see cref="Message"/> travels alongside because the envelope is text and an adapter needs
/// the structure — the identifier to correlate an answer with the question it settles (§4.2),
/// the type, the sender. What it must not do is render those into the model's context itself.
/// </para>
/// </remarks>
public sealed record DeliveredMessage(Message Message, string Envelope);
