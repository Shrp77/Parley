namespace Parley.Core;

/// <summary>
/// A message and the envelope it must be delivered inside (SPEC.md §13).
/// </summary>
/// <param name="Message">The message as stored.</param>
/// <param name="Envelope">
/// The text to place in the receiving agent's context. Contains the payload, and for a Result the
/// Request it answers; neither is placed anywhere else.
/// </param>
/// <param name="Settleable">
/// Beside a Request from the person only: the receiving agent's own open questions to the person in that conversation,
/// oldest first, at most <see cref="Settling.MostListed"/> (SPEC.md §5.6). Null on every other message.
/// </param>
/// <param name="SettleableLeftOut">How many more such questions there are than are listed.</param>
/// <param name="Since">
/// Beside a person's Request the agent had asked back about: what the person has said to it in that conversation since
/// it last spoke on the exchange, oldest first, at most <see cref="Settling.MostSince"/> (SPEC.md §5.6). Null otherwise.
/// </param>
/// <remarks>
/// <para>
/// The pairing exists so that an adapter cannot deliver a participant's words without the framing
/// around them. The broker wraps and never the sender, and handing back the envelope rather than the
/// raw payload is what makes that impossible rather than discouraged.
/// </para>
/// <para>
/// The envelope is produced at delivery and not stored, so correcting its wording protects every
/// message ever sent, and its token is fresh per delivery. <see cref="Message"/> travels alongside
/// because an adapter needs the structure — the exchange identifier, the type, the sender — and must not
/// render those into the model's context itself.
/// </para>
/// </remarks>
public sealed record DeliveredMessage(
    Message Message,
    string Envelope,
    IReadOnlyList<SettleableQuestion>? Settleable = null,
    int SettleableLeftOut = 0,
    IReadOnlyList<AnswerSince>? Since = null);
