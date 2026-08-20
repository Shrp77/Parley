using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// What became of an <c>ask</c>: the exchange and the question that opened it, or the refusal.
/// </summary>
/// <remarks>
/// <para>
/// Wraps <see cref="OpenOutcome"/> rather than replacing it, because admission and saying the
/// question are different failures with different remedies and flattening them would lose that.
/// A refusal means the question was never admitted and the asker should act on what it was
/// handed back — the exchanges it is already waiting on, or the cycle it would have closed.
/// A duplicate means the question was already said inside the deduplication window and the
/// caller should carry on as though this send succeeded, because it did, once.
/// </para>
/// <para>
/// Per SPEC.md §4.2 this carries an identifier and never an answer. The answer arrives through
/// <see cref="Broker.DrainInboxAsync"/>, later, carrying the same identifier.
/// </para>
/// </remarks>
public sealed record AskOutcome
{
    private AskOutcome(OpenOutcome admission, Message? question, bool stored)
    {
        Admission = admission;
        Question = question;
        Stored = stored;
    }

    /// <summary>Gets the admission decision, carrying the refusal when there was one.</summary>
    public OpenOutcome Admission { get; }

    /// <summary>
    /// Gets the exchange that was opened.
    /// </summary>
    /// <value>
    /// The exchange, or <see langword="null"/> if admission refused it. This is the identifier
    /// SPEC.md §4.2 requires <c>ask</c> to return.
    /// </value>
    public Exchange? Exchange => Admission.Exchange;

    /// <summary>
    /// Gets the question as stored.
    /// </summary>
    /// <value>
    /// The question message when the exchange was opened; otherwise <see langword="null"/>.
    /// Non-null exactly when <see cref="Succeeded"/> is <see langword="true"/>.
    /// </value>
    public Message? Question { get; }

    /// <summary>
    /// Gets a value indicating whether this call wrote the question.
    /// </summary>
    /// <value>
    /// <see langword="false"/> when an identical question was already written inside the
    /// deduplication window of SPEC.md §13.1, in which case <see cref="Question"/> is the
    /// earlier one.
    /// </value>
    public bool Stored { get; }

    /// <summary>Gets the reason admission refused the question.</summary>
    /// <value><see cref="OpenRejection.None"/> when it did not.</value>
    public OpenRejection Rejection => Admission.Rejection;

    /// <summary>
    /// Gets a value indicating whether an exchange is now open carrying this question.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="Exchange"/> and <see cref="Question"/> are both
    /// populated.
    /// </value>
    public bool Succeeded => Admission.Succeeded;

    /// <summary>Creates the outcome for a question that was admitted and written.</summary>
    /// <param name="admission">The successful admission.</param>
    /// <param name="question">The question as stored.</param>
    /// <param name="stored">Whether this call wrote it, or deduplication suppressed it.</param>
    /// <returns>An outcome carrying the exchange and the question.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="admission"/> is a refusal.</exception>
    internal static AskOutcome Asked(OpenOutcome admission, Message question, bool stored)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(question);

        if (!admission.Succeeded)
        {
            throw new ArgumentException(
                "A refused admission carries no question.", nameof(admission));
        }

        return new(admission, question, stored);
    }

    /// <summary>Creates the outcome for a question admission refused.</summary>
    /// <param name="admission">The refusal, carrying whatever the asker can act on.</param>
    /// <returns>An outcome carrying no exchange and no question.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="admission"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="admission"/> succeeded.</exception>
    internal static AskOutcome Refused(OpenOutcome admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        if (admission.Succeeded)
        {
            throw new ArgumentException(
                "A successful admission is not a refusal.", nameof(admission));
        }

        return new(admission, question: null, stored: false);
    }
}
