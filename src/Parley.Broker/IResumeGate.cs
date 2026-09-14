using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// Asked whether waking an agent for open work it owes is permitted right now.
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
/// Declining is cheap and loses nothing, which is why a gate may refuse freely. Work an agent is
/// not woken for stays open and is flagged to the person running the deployment (§6), who can
/// start the agent or close the conversation; nothing ends it on a schedule.
/// </para>
/// </remarks>
public interface IResumeGate
{
    /// <summary>
    /// Reports whether the agent owing the given exchange may be woken for it.
    /// </summary>
    /// <param name="exchange">The open exchange the agent owes, being considered.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns><see langword="true"/> to permit the wake.</returns>
    /// <remarks>
    /// <para>
    /// Asked per exchange rather than once per pass, because a budget gate that has just
    /// permitted several wakes may reasonably decline the next.
    /// </para>
    /// <para>
    /// An exception propagates and abandons the pass. That is intentional: this is the host's own
    /// code, so a throw is a defect it needs to see, and a broker that swallowed it would leave
    /// wakes silently not happening. An implementation that cannot reach whatever it consults
    /// should decide what that means and return an answer — declining is safe, because the work
    /// stays open and flagged.
    /// </para>
    /// </remarks>
    ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken = default);
}

/// <summary>
/// A gate that permits every wake.
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
