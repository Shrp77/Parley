namespace Parley.Core;

/// <summary>
/// Why a move on an exchange was refused.
/// </summary>
/// <remarks>
/// Refusals are ordinary control flow, not exceptional conditions. Two participants racing, or one
/// speaking out of turn, are expected events in a concurrent system and are reported as values.
/// </remarks>
public enum TransitionError
{
    /// <summary>No error; the move succeeded.</summary>
    None = 0,

    /// <summary>The exchange is closed and accepts no further moves.</summary>
    ExchangeNotOpen,

    /// <summary>The sender is neither the requester nor the receiver of this exchange.</summary>
    NotAParticipant,

    /// <summary>The sender is a participant, but the other party holds the next move.</summary>
    NotYourTurn,

    /// <summary>The requester tried to send the Result. Only the receiver reports on its own work.</summary>
    OnlyReceiverSendsResult,

    /// <summary>A clarification cap the deployment set has been reached.</summary>
    /// <remarks>
    /// Nothing else follows from it: the exchange stays open, and the receiver sends a Result with
    /// what it has — <c>blocked</c>, naming what it still needs, where that is the truth (§5.2).
    /// </remarks>
    ClarifyDepthExceeded,

    /// <summary>
    /// The Result does not give exactly one outcome for each item the Request carried.
    /// </summary>
    /// <remarks>
    /// Checked by the broker against the stored Request, because an answer to some items closes an
    /// exchange just as firmly as an answer to all of them, and the rest would be gone with no record
    /// that they were skipped.
    /// </remarks>
    OutcomesDoNotMatchItems,

    /// <summary>A participant tried to record <c>closed</c>, which only the broker writes.</summary>
    ClosedIsTheBrokers
}
