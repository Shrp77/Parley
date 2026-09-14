namespace Parley.Core;

/// <summary>
/// The terms an exchange is admitted under.
/// </summary>
/// <param name="UncollectedAfter">How long a Request may go uncollected before it is flagged (§6).</param>
/// <param name="StalledAfter">How long the owing participant may go without a message before it is flagged (§6).</param>
/// <param name="ClarifyDepthCap">Clarifications a receiver may ask for in one exchange, or <see cref="UnboundedClarifications"/> for no cap.</param>
/// <param name="OpenPairCap">Open exchanges one participant may hold to another, directionally, or <see cref="Unbounded"/> for no cap.</param>
/// <remarks>
/// <para>
/// Every value here is stamped onto the exchange when it is admitted and never read from
/// configuration again, so an operator changing a number cannot reach back into work already in
/// flight.
/// </para>
/// <para>
/// <b>Nothing here ends work.</b> The thresholds decide when silence is shown to the person running the
/// deployment, and crossing one changes nothing about the exchange. Both caps are off by default,
/// because a cap set without evidence refuses work at a number nobody measured (principle 2.8); a
/// deployment that observes a flood sets one from that observation.
/// </para>
/// <para>
/// <b>Policy is chosen by the operator and never by an agent.</b> Admission takes a resolved policy,
/// never the name of one, so there is no parameter through which an agent could ask for a larger cap.
/// A deployment may map agents to different policies — an agent whose work runs a full end-to-end suite
/// needs a longer <see cref="StalledAfter"/> — but that mapping is resolved before any agent-facing
/// path is reached.
/// </para>
/// </remarks>
public sealed record ExchangePolicy(
    TimeSpan UncollectedAfter,
    TimeSpan StalledAfter,
    int ClarifyDepthCap,
    int OpenPairCap)
{
    /// <summary>The policy applied when a deployment configures none.</summary>
    /// <remarks>
    /// Fifteen minutes and four hours are §6's defaults, and they are starting estimates to be tuned
    /// against real traffic. Both caps are off.
    /// </remarks>
    public static ExchangePolicy Default { get; } = new(
        UncollectedAfter: TimeSpan.FromMinutes(15),
        StalledAfter: TimeSpan.FromHours(4),
        ClarifyDepthCap: UnboundedClarifications,
        OpenPairCap: Unbounded);

    /// <summary>The largest request cap storage accepts, and the value at which there is none.</summary>
    /// <remarks>
    /// <b>Off is spelled as the top of the band rather than as zero.</b> Storage holds the band with a
    /// <c>CHECK</c> and a trigger that stands down at the ceiling, so a deployment that does set a cap
    /// still has storage holding it (§9.3), and a bug writing a large number picks a poor value inside a
    /// sane band rather than switching the rule off.
    /// </remarks>
    public const int MaximumOpenPairCap = 50;

    /// <summary>The <see cref="OpenPairCap"/> at which no cap applies.</summary>
    /// <remarks>
    /// The same number as <see cref="MaximumOpenPairCap"/>, named separately because the two are
    /// different claims that coincide: what storage accepts, and what off is spelled as.
    /// </remarks>
    public const int Unbounded = MaximumOpenPairCap;

    /// <summary>Gets a value indicating whether this policy caps open Requests to one participant.</summary>
    /// <value><see langword="false"/> at <see cref="Unbounded"/>.</value>
    public bool BoundsRequests => OpenPairCap < Unbounded;

    /// <summary>The largest clarification cap storage accepts, short of switching the cap off.</summary>
    public const int MaximumClarifyDepthCap = 1000;

    /// <summary>The <see cref="ClarifyDepthCap"/> at which clarifications are not capped.</summary>
    /// <remarks>
    /// The largest integer, because zero already means something: no clarification at all, which a
    /// deployment may genuinely want.
    /// </remarks>
    public const int UnboundedClarifications = int.MaxValue;

    /// <summary>Gets a value indicating whether this policy caps clarifications at all.</summary>
    /// <value><see langword="false"/> at <see cref="UnboundedClarifications"/>.</value>
    public bool BoundsClarifications => ClarifyDepthCap != UnboundedClarifications;

    /// <summary>Throws if any value is outside what storage will accept.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    /// <remarks>
    /// The same bounds the schema enforces, checked here so a misconfigured deployment fails at
    /// start-up naming the field rather than on its first Request.
    /// </remarks>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(UncollectedAfter, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(StalledAfter, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfNegative(ClarifyDepthCap);

        if (BoundsClarifications)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ClarifyDepthCap, MaximumClarifyDepthCap);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(OpenPairCap, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OpenPairCap, MaximumOpenPairCap);
    }
}
