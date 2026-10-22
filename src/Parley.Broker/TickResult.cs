using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What one maintenance pass did.
/// </summary>
/// <param name="ConversationsConcluded">Polling conversations closed because their last decline lapsed.</param>
/// <param name="Retention">What retention removed, or <see cref="PruneResult.Nothing"/> when it was not due.</param>
/// <remarks>
/// There is no count of exchanges: the pass does nothing to them. Nothing times an exchange out
/// (principle 2.8), and flags are derived when a person reads them rather than written by a sweep.
/// </remarks>
public sealed record TickResult(
    int ConversationsConcluded,
    PruneResult Retention);
