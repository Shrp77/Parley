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
/// <param name="RoleRequest">
/// On a Result answering one member of a Request to a role, delivered to the agent that sent it: how many the Request
/// reached and how many have answered so far (SPEC.md §5.5). Null on every other message.
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
    IReadOnlyList<AnswerSince>? Since = null,
    RoleRequestProgress? RoleRequest = null);

/// <summary>Where a Request to a role stands: how many agents it reached and how many have answered (SPEC.md §5.5).</summary>
/// <param name="Asked">The members the Request opened an exchange with.</param>
/// <param name="Answered">The members whose exchange a Result closed, complete or blocked.</param>
/// <remarks>
/// Counted from how each member's exchange closed, never from the messages present, so retention pruning a Result's
/// words does not take it out of the count. Whether the work needs every answer is the requester's to decide.
/// </remarks>
public sealed record RoleRequestProgress(int Asked, int Answered);
