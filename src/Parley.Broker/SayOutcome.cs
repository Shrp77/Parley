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
    private SayOutcome(Message? message, bool stored, TransitionError? error)
    {
        Message = message;
        Stored = stored;
        Error = error;
    }

    /// <summary>Gets each exchange a reply named in <c>settles</c> that it could not settle, and why.</summary>
    /// <value>On <see cref="TransitionError.CannotSettle"/>; empty otherwise. Nothing was stored (SPEC.md §5.6).</value>
    public IReadOnlyList<(long Exchange, SettleProblem Problem)> CannotSettle { get; private init; } = [];

    /// <summary>Gets the agent's own questions to the person this reply closed as settled.</summary>
    /// <value>Empty unless the reply named some and was stored.</value>
    public IReadOnlyList<long> Settled { get; private init; } = [];

    /// <summary>Gets the person's own Requests this reply handed back to the agent.</summary>
    /// <value>Empty unless the reply named some and was stored.</value>
    public IReadOnlyList<long> HandedBack { get; private init; } = [];

    /// <summary>Gets the message as stored, or the earlier identical one.</summary>
    /// <value>Non-null exactly when <see cref="Succeeded"/> is <see langword="true"/>.</value>
    public Message? Message { get; }

    /// <summary>Gets a value indicating whether this call wrote the message.</summary>
    /// <value><see langword="false"/> for a refusal and for a retry inside the deduplication window.</value>
    public bool Stored { get; }

    /// <summary>Gets why the move was refused.</summary>
    /// <value>The error, or <see langword="null"/> on success.</value>
    public TransitionError? Error { get; }

    /// <summary>Gets a value indicating whether the move was accepted.</summary>
    /// <value><see langword="true"/> when <see cref="Message"/> is populated.</value>
    public bool Succeeded => Message is not null;

    internal static SayOutcome Said(Message message, bool stored)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message, stored, error: null);
    }

    internal static SayOutcome Said(Message message, IReadOnlyList<long> settled, IReadOnlyList<long> handedBack)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message, stored: true, error: null) { Settled = settled, HandedBack = handedBack };
    }

    internal static SayOutcome RefusedToSettle(IReadOnlyList<(long Exchange, SettleProblem Problem)> problems) =>
        new(message: null, stored: false, TransitionError.CannotSettle) { CannotSettle = problems };

    internal static SayOutcome Refused(TransitionError error) =>
        new(message: null, stored: false, error);
}
