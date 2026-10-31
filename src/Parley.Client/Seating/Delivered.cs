using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>
/// One message that has been handed to a person.
/// </summary>
/// <param name="Message">The message as the broker recorded it.</param>
/// <remarks>
/// <para>
/// Held rather than re-read, because there is nothing to re-read it from: a collection takes what is
/// waiting at least once, as every collection does (SPEC.md §6). This is the surface keeping what a running adapter would keep in a model's context.
/// </para>
/// <para>
/// <b>The §13 envelope is deliberately not kept.</b> The broker builds it so an adapter delivering another
/// participant's words into a <em>model's</em> context cannot forget the framing. There is no model here:
/// the surfaces render the same facts structurally, and a string nothing reads is how a guard lapses.
/// </para>
/// </remarks>
public sealed record Delivered(Message Message)
{
    /// <summary>Gets a value indicating whether this is a Request the person now owes a Result for.</summary>
    /// <value><see langword="true"/> for a Request.</value>
    /// <remarks>It decides whether a Result form is offered, and offering one for anything else invites a refusal.</remarks>
    public bool OwesResult => Message.Type == MessageType.Request;

    /// <summary>Gets a value indicating whether this is a clarification the person now owes a reply to.</summary>
    /// <value><see langword="true"/> for a Clarify.</value>
    public bool OwesReply => Message.Type == MessageType.Clarify;

    /// <summary>Gets a value indicating whether anything is owed on this message at all.</summary>
    /// <value><see langword="true"/> when a Result or a reply is owed.</value>
    public bool Owes => OwesResult || OwesReply;

    /// <summary>The items a Result for this Request must give one outcome each.</summary>
    /// <returns>The one item, <see cref="PayloadShape.WholeRequest"/>; empty when this is not a Request.</returns>
    /// <remarks>
    /// A Request is answered as a whole, as the broker's own close answers one (SPEC.md §5.1), so a person
    /// holding it always has a form to answer it by.
    /// </remarks>
    public IReadOnlyList<string> Items() => OwesResult ? [PayloadShape.WholeRequest] : [];
}
