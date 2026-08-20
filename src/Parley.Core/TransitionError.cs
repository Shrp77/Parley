namespace Parley.Core;

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
    ExchangeNotOrphaned,

    /// <summary>
    /// An answer was attempted with <see cref="Resolution.Ghosted"/>.
    /// </summary>
    /// <remarks>
    /// A ghost asserts that nobody answered, so it cannot arrive on an answer. The schema
    /// refuses this as well; refusing it here means the caller learns what it did wrong rather
    /// than meeting a constraint violation raised three layers further down.
    /// </remarks>
    GhostIsNotAnAnswer
}
