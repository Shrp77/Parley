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
    /// re-registers, the exchange becomes eligible for replay into its inbox — eligible,
    /// not guaranteed: SPEC.md section 14 makes resumption a configured policy gated by the
    /// broker, because a restart carries no evidence of whether the operator wanted the
    /// conversation stopped or continued.
    /// </remarks>
    Orphaned = 2
}
