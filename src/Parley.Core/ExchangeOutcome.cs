namespace Parley.Core;

/// <summary>
/// How an exchange as a whole closed: the rolled-up outcome of its Result (SPEC.md §5.3).
/// </summary>
/// <remarks>
/// The least favourable reading, deliberately. It is what survives once retention prunes the
/// message text, and a rollup taken the other way would let one finished item hide four blocked
/// ones — so telemetry would report a healthier deployment the more its agents got stuck.
/// </remarks>
public enum ExchangeOutcome
{
    /// <summary>Every item was done, answered or acknowledged.</summary>
    Complete = 0,

    /// <summary>At least one item was blocked.</summary>
    Blocked = 1,

    /// <summary>The conversation was closed before the receiver sent its Result.</summary>
    Closed = 2,

    /// <summary>
    /// The requester's question to the person, closed by the requester because something the person wrote later
    /// settled it (SPEC.md §5.6). No Result was written for it.
    /// </summary>
    Settled = 3
}
