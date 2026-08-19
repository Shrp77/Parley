using Parley.Core;

namespace Parley.Store;

/// <summary>
/// The result of appending a message: either the message that was stored, or the earlier one
/// that made it a duplicate.
/// </summary>
/// <remarks>
/// <para>
/// A refused duplicate is not an error. At-least-once transport means a retry is ordinary
/// traffic, and the caller's correct response is to carry on as though the send succeeded —
/// which it did, once. Reporting it as an outcome rather than an exception says that plainly,
/// and matches how <see cref="OpenOutcome"/> treats a refused question.
/// </para>
/// <para>
/// The constructor is private and the factories are the only way in, for the same reason as
/// <see cref="OpenOutcome"/>: callers read <see cref="Stored"/> and then dereference
/// <see cref="Message"/> without a null check, so that pairing is an invariant rather than a
/// convention.
/// </para>
/// </remarks>
public sealed record AppendOutcome
{
    private AppendOutcome(Message message, bool stored)
    {
        Message = message;
        Stored = stored;
    }

    /// <summary>
    /// Gets the message this outcome concerns.
    /// </summary>
    /// <value>
    /// The newly appended message when <see cref="Stored"/> is <see langword="true"/>;
    /// otherwise the earlier message already holding this identity. Never
    /// <see langword="null"/> — a duplicate is always a duplicate <em>of</em> something.
    /// </value>
    public Message Message { get; }

    /// <summary>
    /// Gets a value indicating whether the message was written.
    /// </summary>
    /// <value>
    /// <see langword="true"/> if this call appended the message; <see langword="false"/> if an
    /// identical one already existed inside the deduplication window.
    /// </value>
    public bool Stored { get; }

    /// <summary>
    /// Creates the outcome for a message that was appended.
    /// </summary>
    /// <param name="message">The message as stored, carrying its assigned identity.</param>
    /// <returns>An outcome reporting a successful append.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public static AppendOutcome Appended(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message, stored: true);
    }

    /// <summary>
    /// Creates the outcome for a message suppressed as a duplicate.
    /// </summary>
    /// <param name="existing">The earlier message with the same identity.</param>
    /// <returns>An outcome reporting that nothing was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="existing"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The earlier message is returned rather than discarded so the caller can report what it
    /// collided with. A bare "duplicate" tells an operator nothing about which send was the
    /// retry.
    /// </remarks>
    public static AppendOutcome Duplicate(Message existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return new(existing, stored: false);
    }
}
