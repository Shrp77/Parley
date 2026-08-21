using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// Why a notification was refused.
/// </summary>
public enum NotifyRejection
{
    /// <summary>No refusal.</summary>
    None = 0,

    /// <summary>
    /// The recipient is not being routed to: it could not be spawned, or it has been retired.
    /// </summary>
    /// <remarks>
    /// The same rule admission applies to a question (§14), and for the same reason: a message
    /// addressed to an agent nothing will route to is a write with no reader. It matters less
    /// here — a notification creates no debt and nobody waits on it — but a sender told plainly
    /// that a peer is gone can stop announcing to it, which it cannot do if the writes silently
    /// succeed.
    /// </remarks>
    RecipientUnroutable,

    /// <summary>
    /// The sender has itself been retired.
    /// </summary>
    /// <remarks>
    /// Symmetric with <see cref="OpenRejection.AskerRetired"/>. A retired agent is one an
    /// operator has withdrawn, and it announcing events to peers is exactly the activity the
    /// withdrawal was meant to stop.
    /// </remarks>
    SenderRetired,

    /// <summary>
    /// The notification names a conversation that has already concluded.
    /// </summary>
    /// <remarks>
    /// SPEC.md §5.0 says a closed conversation stays closed, and states the hazard as a message
    /// left "in a closed discussion nobody may add to". It words the refusal in terms of a
    /// Question, because that is what takes a slot and grows membership — but a notification
    /// naming a concluded identifier adds to it just the same, and the asymmetry was reachable:
    /// a question was refused and a notification alongside it was not.
    /// </remarks>
    ConversationClosed
}

/// <summary>
/// What became of a <c>notify</c>: the event as stored, or the refusal.
/// </summary>
/// <remarks>
/// <para>
/// Simpler than <see cref="SayOutcome"/> because a notification moves nothing. SPEC.md §5.4:
/// it opens no exchange, takes no slot, and creates no debt for anybody — so there is no
/// transition to refuse and no turn to be out of.
/// </para>
/// <para>
/// That also makes <see cref="Stored"/> matter more here than anywhere else. §13.1 notes that a
/// repeated question is refused by the pair slot, a repeated clarification by the turn check and
/// a repeated answer by the exchange no longer being open — <c>Notify</c> is the one type with
/// no such guard, and deduplication is the only thing standing between an at-least-once
/// transport and the same event arriving twice.
/// </para>
/// </remarks>
public sealed record NotifyOutcome
{
    private NotifyOutcome(Message? message, bool stored, NotifyRejection rejection)
    {
        Message = message;
        Stored = stored;
        Rejection = rejection;
    }

    /// <summary>
    /// Gets the notification as stored.
    /// </summary>
    /// <value>
    /// The message when it was accepted; otherwise <see langword="null"/>. Non-null exactly when
    /// <see cref="Succeeded"/> is <see langword="true"/>.
    /// </value>
    public Message? Message { get; }

    /// <summary>
    /// Gets a value indicating whether this call wrote the notification.
    /// </summary>
    /// <value>
    /// <see langword="false"/> when an identical one was already written inside the §13.1
    /// window, in which case <see cref="Message"/> is the earlier one. The caller should carry
    /// on as though the send succeeded, because it did — once.
    /// </value>
    public bool Stored { get; }

    /// <summary>Gets why the notification was refused.</summary>
    /// <value><see cref="NotifyRejection.None"/> when it was not.</value>
    public NotifyRejection Rejection { get; }

    /// <summary>Gets a value indicating whether the notification was accepted.</summary>
    public bool Succeeded => Rejection == NotifyRejection.None;

    /// <summary>Creates the outcome for an accepted notification.</summary>
    /// <param name="message">The event as stored, or the earlier one it duplicates.</param>
    /// <param name="stored">Whether this call wrote it.</param>
    /// <returns>An outcome carrying the message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    internal static NotifyOutcome Sent(Message message, bool stored)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message, stored, NotifyRejection.None);
    }

    /// <summary>Creates a refusal.</summary>
    /// <param name="rejection">Why it was refused.</param>
    /// <returns>An outcome carrying no message.</returns>
    internal static NotifyOutcome Refused(NotifyRejection rejection) =>
        new(message: null, stored: false, rejection);
}
