using Parley.Core;

namespace Parley.Web.Seating;

/// <summary>
/// One message that has been handed to a person.
/// </summary>
/// <param name="Message">The message as the broker recorded it.</param>
/// <remarks>
/// <para>
/// Held rather than re-read, because there is nothing to re-read it from. A drain takes what is
/// waiting exactly once — that is what marks it delivered and starts the working window of §6 —
/// so an agent that threw the result away has no second copy on its own surface. This is the
/// console keeping what a running adapter would keep in a model's context.
/// </para>
/// <para>
/// <b>The §13 envelope is deliberately not kept.</b> The broker builds it so that an adapter
/// delivering a peer's words into a <em>model's</em> context cannot forget the framing that marks
/// them as data. There is no model here: the inbox renders the same facts structurally — who
/// sent it, what kind of message it is, what they claim produced it, when, and which conversation
/// — and <c>InboxTests</c> pins that. Carrying a string nothing reads would be inert data, and
/// inert data is how a guard lapses: the day something reads it, the framing that was correct
/// for the old reader is silently the wrong one.
/// </para>
/// </remarks>
public sealed record Delivered(Message Message)
{
    /// <summary>Whether this is a question the person now owes an answer to.</summary>
    /// <remarks>
    /// A question and a clarification both put the turn on the receiver; an answer or a notice
    /// leave nothing owed. The distinction is what decides whether an answer form is offered,
    /// and offering one for a message nobody may answer would be an invitation to a refusal.
    /// </remarks>
    public bool Owes =>
        Message.Type is MessageType.Question or MessageType.Clarify
        && Message.ExchangeId is not null;
}
