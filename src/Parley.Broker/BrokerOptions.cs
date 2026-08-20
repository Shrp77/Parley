using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What a deployment decides, as opposed to what an exchange carries.
/// </summary>
/// <param name="ExchangePolicy">The terms new exchanges are admitted under.</param>
/// <param name="ResumePolicy">What a restart does to the exchanges it interrupted.</param>
/// <param name="ResumeGate">Consulted before each replay under <see cref="Core.ResumePolicy.Always"/>.</param>
/// <remarks>
/// <para>
/// The division here is not arbitrary. <see cref="ExchangePolicy"/> is stamped onto every
/// exchange at admission and never read again, so changing it affects exchanges opened
/// afterwards and never reaches back. The other two are consulted live, at restart and at
/// replay, so changing them takes effect immediately and applies to work already in flight.
/// </para>
/// <para>
/// That is the reason they are separate types rather than one settings object: an operator
/// needs to know which numbers reach backwards and which do not, and a single bag of settings
/// would make that invisible.
/// </para>
/// </remarks>
public sealed record BrokerOptions(
    ExchangePolicy ExchangePolicy,
    ResumePolicy ResumePolicy,
    IResumeGate ResumeGate)
{
    /// <summary>
    /// The configuration applied when a deployment specifies none.
    /// </summary>
    /// <remarks>
    /// <see cref="Core.ResumePolicy.Never"/> because SPEC.md §14 makes it the safe default for
    /// interactive use, and a default should be safe rather than convenient: work resuming that
    /// somebody stopped on purpose is the surprise worth avoiding. An unattended deployment
    /// chooses <see cref="Core.ResumePolicy.Always"/> deliberately, which is the right way
    /// round for a choice this consequential.
    /// </remarks>
    public static BrokerOptions Default { get; } = new(
        Core.ExchangePolicy.Default,
        Core.ResumePolicy.Never,
        AlwaysResume.Instance);

    /// <summary>
    /// Throws if the configuration is unusable.
    /// </summary>
    /// <exception cref="ArgumentNullException">A required member is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ExchangePolicy"/> carries a value outside what storage will accept, or
    /// <see cref="ResumePolicy"/> is not a defined value.
    /// </exception>
    /// <remarks>
    /// Checked when the broker is constructed rather than when a question first arrives, so a
    /// misconfigured deployment fails at start-up naming the field.
    /// </remarks>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ExchangePolicy);
        ArgumentNullException.ThrowIfNull(ResumeGate);

        ExchangePolicy.Validate();

        if (!Enum.IsDefined(ResumePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ResumePolicy), ResumePolicy, "Not a defined resume policy.");
        }
    }
}

