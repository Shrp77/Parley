namespace Parley.Core;

/// <summary>
/// The lifecycle state of an <see cref="Exchange"/>.
/// </summary>
public enum ExchangeState
{
    /// <summary>
    /// Awaiting a response. Holds the pair slot and contributes an edge to the wait-for
    /// graph.
    /// </summary>
    Open = 0,

    /// <summary>
    /// Resolved by an answer. Holds no pair slot and blocks nothing. Terminal.
    /// </summary>
    Closed = 1,

    /// <summary>
    /// Abandoned by time-out or participant disconnect, but preserved for replay.
    /// </summary>
    /// <remarks>
    /// Orphaning releases the pair slot so a vanished agent cannot hold a lock indefinitely,
    /// while keeping the question so it is not silently lost. When the owing agent
    /// re-registers, the exchange is resumed and the pending question replayed into its
    /// inbox.
    /// </remarks>
    Orphaned = 2
}

/// <summary>
/// The kind of message travelling between two agents.
/// </summary>
/// <remarks>
/// There are exactly four, and the set is closed by design. Agents do not exchange
/// pleasantries, so every additional type would be a new way to spend tokens without
/// producing a decision.
/// </remarks>
public enum MessageType
{
    /// <summary>
    /// Opens an exchange and claims the pair slot. Carries one or more sub-questions, the
    /// asker's intent, and what the asker will do with each possible answer.
    /// </summary>
    Question = 0,

    /// <summary>
    /// Requests or supplies detail within an already-open exchange.
    /// </summary>
    /// <remarks>
    /// Deliberately claims no slot of its own. If it did, an answerer needing detail could
    /// not ask for it, because the pair's only slot is held by the very question being
    /// clarified — a deadlock inside the deadlock fix.
    /// </remarks>
    Clarify = 1,

    /// <summary>
    /// Resolves and closes an exchange, releasing the pair slot.
    /// </summary>
    Answer = 2,

    /// <summary>
    /// A fire-and-forget event. Opens no exchange, claims no slot, and expects no reply.
    /// </summary>
    /// <remarks>
    /// Typically a contract change announcement. Expected to be the highest-volume traffic
    /// in a mature deployment.
    /// </remarks>
    Notify = 3
}

/// <summary>
/// The outcome recorded against a single sub-question when an exchange is answered.
/// </summary>
public enum Resolution
{
    /// <summary>
    /// Resolved from the answering agent's own authority.
    /// </summary>
    Answered = 0,

    /// <summary>
    /// Outside the answering agent's boundary. A redirect target should accompany this.
    /// </summary>
    /// <remarks>
    /// Not a separate message type — it is an answer — but it must remain distinguishable,
    /// because it is the system's most useful diagnostic. A cluster of these between two
    /// agents means the ownership map is wrong, which is a defect that would otherwise go
    /// unnoticed.
    /// </remarks>
    OutOfScope = 1,

    /// <summary>
    /// Within the answering agent's boundary, but genuinely undetermined.
    /// </summary>
    /// <remarks>
    /// Preferable to a confident guess. The asker will act on whatever comes back, so an
    /// admitted gap is cheaper than a fabrication. Also produced automatically when the
    /// clarification depth cap is reached.
    /// </remarks>
    Unknown = 2
}

/// <summary>
/// The reason a state transition was refused.
/// </summary>
/// <remarks>
/// Refusals are ordinary control flow, not exceptional conditions. Two agents racing for the
/// same pair slot, or an agent speaking out of turn, are expected events in a concurrent
/// system and are reported as values rather than thrown.
/// </remarks>
public enum TransitionError
{
    /// <summary>No error; the transition succeeded.</summary>
    None = 0,

    /// <summary>
    /// The exchange is closed or orphaned and accepts no further messages.
    /// </summary>
    ExchangeNotOpen,

    /// <summary>
    /// The sending agent is neither the asker nor the answerer of this exchange.
    /// </summary>
    NotAParticipant,

    /// <summary>
    /// The sending agent is a participant, but does not currently owe a response.
    /// </summary>
    NotYourTurn,

    /// <summary>
    /// A closing answer was attempted by the asker. Only the original answerer may close an
    /// exchange, regardless of who holds the turn.
    /// </summary>
    OnlyAnswererMayAnswer,

    /// <summary>
    /// The clarification depth cap was reached.
    /// </summary>
    /// <remarks>
    /// The broker responds by forcing an answer of <see cref="Resolution.Unknown"/> through
    /// <see cref="Exchange.ForceUnknown"/>. Left unbounded, two models will refine a question
    /// toward mutual precision indefinitely and never produce a decision.
    /// </remarks>
    ClarifyDepthExceeded,

    /// <summary>
    /// Resumption was attempted on an exchange that is not orphaned.
    /// </summary>
    ExchangeNotOrphaned
}

/// <summary>
/// The result of attempting a state transition on an <see cref="Exchange"/>.
/// </summary>
/// <param name="Succeeded">Whether the transition was applied.</param>
/// <param name="Error">
/// The reason for refusal, or <see cref="TransitionError.None"/> on success.
/// </param>
public readonly record struct TransitionResult(bool Succeeded, TransitionError Error)
{
    /// <summary>
    /// Gets the result representing a successfully applied transition.
    /// </summary>
    public static TransitionResult Ok { get; } = new(true, TransitionError.None);

    /// <summary>
    /// Creates a result representing a refused transition.
    /// </summary>
    /// <param name="error">The reason for refusal.</param>
    /// <returns>A failed result carrying <paramref name="error"/>.</returns>
    public static TransitionResult Fail(TransitionError error) => new(false, error);
}
