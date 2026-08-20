using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// Asked whether replaying an orphaned exchange is permitted right now.
/// </summary>
/// <remarks>
/// <para>
/// Defined by its contract rather than its implementation, deliberately. A budget guard that
/// declines above some fraction of a token quota is the motivating case, but it is <em>an</em>
/// implementation: SPEC.md §14 refuses to name tokens in the protocol, because usage means
/// different things across the tiers it must serve. A metered API key, a subscription seat and
/// a local model whose marginal cost is zero are not comparable, and a percentage threshold is
/// meaningless for the third.
/// </para>
/// <para>
/// The gate sits beyond the resume policy rather than inside it, so an operator can express
/// "resume, but not if it will cost me too much" without that condition becoming a protocol
/// rule. <see cref="Core.ResumePolicy.Always"/> says what the operator wants; this says whether the
/// moment allows it.
/// </para>
/// <para>
/// Declining is cheap and self-correcting, which is why a gate may refuse freely. An exchange
/// that is not replayed stays orphaned and is ghosted when its grace period expires, so the
/// asker is told nobody is coming rather than waiting indefinitely.
/// </para>
/// </remarks>
public interface IResumeGate
{
    /// <summary>
    /// Reports whether the given exchange may be replayed.
    /// </summary>
    /// <param name="exchange">The orphaned exchange being considered.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns><see langword="true"/> to permit the replay.</returns>
    /// <remarks>
    /// <para>
    /// Asked per exchange rather than once per sweep, because a budget gate that has just
    /// permitted several replays may reasonably decline the next.
    /// </para>
    /// <para>
    /// An exception propagates and abandons the replay pass. That is intentional: this is the
    /// host's own code, so a throw is a defect it needs to see, and a broker that swallowed it
    /// would leave replays silently not happening. An implementation that cannot reach whatever
    /// it consults should decide what that means and return an answer — declining is safe,
    /// because an exchange that is not replayed is ghosted when its grace period expires.
    /// </para>
    /// </remarks>
    ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken = default);
}

/// <summary>
/// A gate that permits every replay.
/// </summary>
/// <remarks>
/// The default, and honest about what it is: a deployment with no budget guard has no basis on
/// which to decline, and a gate that refused arbitrarily would be worse than none. What makes
/// this safe as a default is that it is only ever consulted under
/// <see cref="Core.ResumePolicy.Always"/>, which an operator has to choose.
/// </remarks>
public sealed class AlwaysResume : IResumeGate
{
    /// <summary>The single instance; this type holds no state.</summary>
    public static AlwaysResume Instance { get; } = new();

    private AlwaysResume()
    {
    }

    /// <inheritdoc />
    public ValueTask<bool> MayResumeAsync(
        Exchange exchange,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}
