using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What became of a <c>clarify</c> or an <c>answer</c> on an exchange already open.
/// </summary>
/// <remarks>
/// <para>
/// Three failures, kept apart because they call for three different things. A
/// <see cref="TransitionError"/> means the exchange refused the move and the agent is wrong
/// about the state of the world — it does not hold the turn, or the exchange has already
/// closed — and retrying unchanged will fail identically. <see cref="Stale"/> means another
/// writer got there first and the agent should reload and decide again, since the move it
/// intended may no longer be legal. Neither is an exception, because both are ordinary
/// outcomes of agents working concurrently.
/// </para>
/// <para>
/// A missing exchange is reported as <see cref="TransitionError.ExchangeNotOpen"/> rather than
/// as its own case. An agent naming an identifier that does not exist and one naming an
/// exchange that has closed are told the same thing deliberately: the difference is only
/// visible to something that can enumerate exchanges, and telling an agent which identifiers
/// exist would leak the log that SPEC.md §10.1 refuses to expose.
/// </para>
/// </remarks>
public sealed record SayOutcome
{
    private SayOutcome(Message? message, bool stored, TransitionError? error, bool stale)
    {
        Message = message;
        Stored = stored;
        Error = error;
        Stale = stale;
    }

    /// <summary>
    /// Gets the message as stored.
    /// </summary>
    /// <value>
    /// The message when the move was accepted; otherwise <see langword="null"/>. Non-null
    /// exactly when <see cref="Succeeded"/> is <see langword="true"/>.
    /// </value>
    public Message? Message { get; }

    /// <summary>
    /// Gets a value indicating whether this call wrote the message.
    /// </summary>
    /// <value>
    /// <see langword="false"/> when an identical message was already written inside the
    /// deduplication window of SPEC.md §13.1, in which case <see cref="Message"/> is the
    /// earlier one.
    /// </value>
    public bool Stored { get; }

    /// <summary>Gets why the exchange refused the move.</summary>
    /// <value>The refusal, or <see langword="null"/> if the exchange did not refuse it.</value>
    public TransitionError? Error { get; }

    /// <summary>
    /// Gets a value indicating whether another writer changed the exchange first.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the move was legal against the state read but the write was
    /// refused as stale. Reload and decide again.
    /// </value>
    public bool Stale { get; }

    /// <summary>Gets a value indicating whether the move was applied.</summary>
    /// <value><see langword="true"/> when <see cref="Message"/> is populated.</value>
    public bool Succeeded => Message is not null;

    /// <summary>Creates the outcome for a move that was applied.</summary>
    /// <param name="message">The message as stored.</param>
    /// <param name="stored">Whether this call wrote it, or deduplication suppressed it.</param>
    /// <returns>An outcome carrying the message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    internal static SayOutcome Said(Message message, bool stored)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message, stored, error: null, stale: false);
    }

    /// <summary>Creates the outcome for a move the exchange refused.</summary>
    /// <param name="error">Why it was refused.</param>
    /// <returns>An outcome carrying the refusal.</returns>
    internal static SayOutcome Refused(TransitionError error) =>
        new(message: null, stored: false, error, stale: false);

    /// <summary>Creates the outcome for a move another writer overtook.</summary>
    /// <returns>An outcome reporting that the caller must reload.</returns>
    internal static SayOutcome Overtaken() =>
        new(message: null, stored: false, error: null, stale: true);
}
