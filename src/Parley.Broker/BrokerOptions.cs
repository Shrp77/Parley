using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What a deployment decides, as opposed to what an exchange carries.
/// </summary>
/// <param name="ExchangePolicy">The terms new exchanges are admitted under.</param>
/// <param name="ResumePolicy">What a restart does to the exchanges it interrupted.</param>
/// <param name="ResumeGate">Consulted before each replay under <see cref="Core.ResumePolicy.Always"/>.</param>
/// <param name="LivenessLease">
/// How long an agent counts as attached after it was last heard from.
/// </param>
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
    IResumeGate ResumeGate,
    TimeSpan LivenessLease)
{
    /// <summary>
    /// How long an agent counts as attached when a deployment specifies nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Liveness is a lease rather than a flag because a flag cannot be cleared by the process
    /// that stops existing. A crashed session leaves <c>live</c> set for ever, which makes the
    /// registry lie and makes the attach sequence of SPEC.md §14 decline to release debts whose
    /// worker is provably gone. A lease expires on its own.
    /// </para>
    /// <para>
    /// It also fixes what a flag cannot express: an agent may have two sessions open, and the
    /// second one closing marks the agent offline while the first is still working. Under a
    /// lease the first session's own heartbeat keeps it current, so nothing concludes it has
    /// gone.
    /// </para>
    /// <para>
    /// Five minutes is chosen against the sweep, not against human patience: the lease must
    /// comfortably exceed how often a host ticks, or a live agent's lease lapses between
    /// heartbeats and the next attach releases work somebody is doing. A host that ticks less
    /// often than this must widen it — see <see cref="Validate"/>, which cannot check that from
    /// here, and the adapter, which derives one from the other so the two cannot disagree.
    /// </para>
    /// </remarks>
    public static TimeSpan DefaultLivenessLease { get; } = TimeSpan.FromMinutes(5);

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
        AlwaysResume.Instance,
        DefaultLivenessLease);

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

        // Zero would make every agent permanently expired, so every attach would release every
        // debt of every session including its own — which is the failure this whole mechanism
        // exists to prevent, reached by setting one number to nothing.
        if (LivenessLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LivenessLease), LivenessLease, "The liveness lease must be positive.");
        }
    }
}

