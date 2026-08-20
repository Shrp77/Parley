using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What became of a <c>clarify</c> or an <c>answer</c> on an exchange already open.
/// </summary>
/// <remarks>
/// <para>
/// One failure, not two. A <see cref="TransitionError"/> means the exchange refused the move
/// and the agent is wrong about the state of the world — it does not hold the turn, or the
/// exchange has already closed — so retrying unchanged will fail identically. It is an outcome
/// rather than an exception because agents working concurrently produce it in ordinary
/// operation.
/// </para>
/// <para>
/// There is deliberately no staleness here, though the write underneath is guarded by a version
/// stamp. The read and the write share one <c>IMMEDIATE</c> transaction, so nothing can change
/// the row in between, and a race between two brokers resolves one step earlier: the loser
/// reads what the winner committed and is refused as
/// <see cref="TransitionError.ExchangeNotOpen"/>. An earlier draft exposed a <c>Stale</c> flag
/// that could never be true, which is worse than an omission — every adapter would have written
/// a reload-and-retry path that no test could reach.
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
    private SayOutcome(Message? message, bool stored, TransitionError? error)
    {
        Message = message;
        Stored = stored;
        Error = error;
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
    /// Gets a value indicating whether this call wrote the message and moved the exchange.
    /// </summary>
    /// <value>
    /// <see langword="false"/> when an identical message was already written inside the
    /// deduplication window of SPEC.md §13.1, in which case <see cref="Message"/> is the earlier
    /// one and <em>the exchange was not moved either</em>.
    /// </value>
    /// <remarks>
    /// The second half is the part worth stating. A suppressed message means the transition it
    /// announces was already applied by the send this one duplicates, so applying it again would
    /// charge the clarify cap twice and hand the turn to a peer whose inbox gained nothing —
    /// leaving it holding an obligation it cannot see. <see cref="Succeeded"/> is still
    /// <see langword="true"/>, because from the sender's side the send did succeed: once.
    /// </remarks>
    public bool Stored { get; }

    /// <summary>Gets why the exchange refused the move.</summary>
    /// <value>The refusal, or <see langword="null"/> if the exchange did not refuse it.</value>
    public TransitionError? Error { get; }

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
        return new(message, stored, error: null);
    }

    /// <summary>Creates the outcome for a move the exchange refused.</summary>
    /// <param name="error">Why it was refused.</param>
    /// <returns>An outcome carrying the refusal.</returns>
    internal static SayOutcome Refused(TransitionError error) =>
        new(message: null, stored: false, error);
}
