using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// What one broker tick did.
/// </summary>
/// <param name="Exchanges">What the exchange sweep orphaned, ghosted, and lost to contention.</param>
/// <param name="ConversationsConcluded">
/// Conversations closed because every polled participant agreed, or fell silent long enough to
/// be taken as agreeing.
/// </param>
/// <remarks>
/// A wrapper rather than three more fields on <see cref="ReapResult"/>, because the two sweeps
/// answer to different sections and would drift if merged. <see cref="ReapResult"/> is the
/// §6 and §14 deadline machinery and lives in the store beside the repository it reads;
/// closure is §5.0 consensus, runs on a different clock, and is decided from the registry as
/// much as from any exchange. A single record would have made <c>Reaper</c> the place both
/// were implemented, which is how a type acquires a second reason to change.
/// </remarks>
public sealed record TickResult(ReapResult Exchanges, int ConversationsConcluded)
{
    /// <summary>A tick that found nothing to do.</summary>
    public static TickResult Nothing { get; } = new(ReapResult.Nothing, 0);
}
