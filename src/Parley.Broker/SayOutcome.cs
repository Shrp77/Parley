using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What became of a Clarify or a Result: the message as stored, or why the exchange refused it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Stored"/> is separate from <see cref="Succeeded"/> because a transport retry of a move
/// already applied is a success that wrote nothing (§13.1): the caller should carry on as though it sent,
/// because it did, once.
/// </para>
/// <para>
/// A refused exchange that does not exist is reported as not open, so identifiers cannot be probed for.
/// </para>
/// </remarks>
public sealed record SayOutcome
{
    private SayOutcome(Message? message, bool stored, TransitionError? error, IReadOnlyList<string> expectedItems)
    {
        Message = message;
        Stored = stored;
        Error = error;
        ExpectedItems = expectedItems;
    }

    /// <summary>Gets the message as stored, or the earlier identical one.</summary>
    /// <value>Non-null exactly when <see cref="Succeeded"/> is <see langword="true"/>.</value>
    public Message? Message { get; }

    /// <summary>Gets a value indicating whether this call wrote the message.</summary>
    /// <value><see langword="false"/> for a refusal and for a retry inside the deduplication window.</value>
    public bool Stored { get; }

    /// <summary>Gets why the move was refused.</summary>
    /// <value>The error, or <see langword="null"/> on success.</value>
    public TransitionError? Error { get; }

    /// <summary>Gets the item ids a Result must answer, when it did not answer exactly those.</summary>
    /// <value>On <see cref="TransitionError.OutcomesDoNotMatchItems"/>; empty otherwise.</value>
    /// <remarks>
    /// What makes that refusal something the sender can correct in one move (§7.3). The Request's own ids a Result can
    /// carry, or <see cref="PayloadShape.WholeRequest"/> alone when it carried none — so on a question carried from 0.4
    /// this is not the list of ids the Request holds (§5.1).
    /// </remarks>
    public IReadOnlyList<string> ExpectedItems { get; }

    /// <summary>Gets a value indicating whether the move was accepted.</summary>
    /// <value><see langword="true"/> when <see cref="Message"/> is populated.</value>
    public bool Succeeded => Message is not null;

    internal static SayOutcome Said(Message message, bool stored)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message, stored, error: null, []);
    }

    internal static SayOutcome Refused(TransitionError error, IReadOnlyList<string>? expectedItems = null) =>
        new(message: null, stored: false, error, expectedItems ?? []);
}
