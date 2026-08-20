namespace Parley.Core;

/// <summary>
/// The terms an exchange is admitted under.
/// </summary>
/// <param name="DeliveryWindow">How long the question may go uncollected.</param>
/// <param name="WorkingWindow">How long an answer may take once the question is collected.</param>
/// <param name="GracePeriod">How long an orphan is kept before it is ghosted.</param>
/// <param name="ClarifyDepthCap">Clarifications permitted before the broker forces an answer.</param>
/// <param name="OpenPairCap">Open exchanges one agent may hold with one peer, directionally.</param>
/// <remarks>
/// <para>
/// Every value here is stamped onto the exchange when it is admitted and never read again from
/// configuration. That is what makes this a policy rather than a settings object: an exchange
/// reproduces its own behaviour from its own row, so an operator changing a number cannot reach
/// back and re-time work already in flight. Lengthening the grace period would otherwise revive
/// orphans already past the old one, and shortening it would ghost a batch of live ones at once.
/// </para>
/// <para>
/// <b>Policy is chosen by the operator and never by an agent.</b> The two caps are controls
/// rather than tuning knobs — SPEC.md §7.1 grounds <see cref="OpenPairCap"/> in the same
/// context-poisoning defence that principle 2.2 makes of the message size limit, and
/// <see cref="ClarifyDepthCap"/> exists because two models will refine a question toward mutual
/// precision indefinitely. A control the controlled party selects is not a control.
/// </para>
/// <para>
/// That constraint is expressed structurally rather than trusted: admission takes a resolved
/// policy, never the name of one. There is consequently no parameter through which an agent
/// could ask for a longer window or a larger cap, because resolution happens before any
/// agent-facing path is reached. Adding an overload that accepts a policy name would undo this
/// quietly, which is the reason it is written down here.
/// </para>
/// <para>
/// A deployment may define several named policies and map agents to them — an agent whose
/// answers require a full end-to-end suite genuinely needs a longer working window than one
/// answering from a contracts directory. That mapping is manifest configuration and is the
/// host's concern; nothing in this type or in storage needs to change to support it, because
/// the values are already carried per exchange.
/// </para>
/// </remarks>
public sealed record ExchangePolicy(
    TimeSpan DeliveryWindow,
    TimeSpan WorkingWindow,
    TimeSpan GracePeriod,
    int ClarifyDepthCap,
    int OpenPairCap)
{
    /// <summary>
    /// The policy applied when a deployment configures none.
    /// </summary>
    /// <remarks>
    /// Every number here is a starting estimate meant to be tuned against real traffic rather
    /// than argued about in advance, and SPEC.md §17 open question 4 says as much about the
    /// clarification cap specifically. They are defaults so that a deployment can start without
    /// deciding five things it has no evidence about yet.
    /// </remarks>
    public static ExchangePolicy Default { get; } = new(
        DeliveryWindow: TimeSpan.FromMinutes(15),
        WorkingWindow: TimeSpan.FromHours(4),
        GracePeriod: TimeSpan.FromHours(3),
        ClarifyDepthCap: 3,
        OpenPairCap: 5);

    /// <summary>
    /// The shortest grace period storage will accept.
    /// </summary>
    /// <remarks>
    /// Bounded because both ends defeat the rule rather than tuning it. At zero an orphan is
    /// ghosted the instant it is created, so the resume policy in SPEC.md §14 never runs and
    /// <c>always</c> becomes indistinguishable from <c>never</c>.
    /// </remarks>
    public static TimeSpan MinimumGracePeriod { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest grace period storage will accept.
    /// </summary>
    /// <remarks>
    /// At the top end the exchange effectively never closes, which restores the unbounded
    /// retention that ghosting exists to remove.
    /// </remarks>
    public static TimeSpan MaximumGracePeriod { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The largest open-question cap storage will accept.
    /// </summary>
    /// <remarks>
    /// A bug that wrote a large number would disable the cap while leaving every constraint
    /// satisfied and nothing to notice, so the worst it can do is pick a poor value inside a
    /// sane band rather than switch the rule off.
    /// </remarks>
    public const int MaximumOpenPairCap = 50;

    /// <summary>
    /// Throws if any value is outside what storage will accept.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    /// <remarks>
    /// The same bounds the schema enforces, checked here so a misconfigured deployment fails at
    /// start-up naming the field, rather than on the first question with a constraint violation
    /// raised several layers below the mistake.
    /// </remarks>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DeliveryWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(WorkingWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(GracePeriod, MinimumGracePeriod);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GracePeriod, MaximumGracePeriod);
        ArgumentOutOfRangeException.ThrowIfNegative(ClarifyDepthCap);
        ArgumentOutOfRangeException.ThrowIfLessThan(OpenPairCap, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OpenPairCap, MaximumOpenPairCap);
    }
}
