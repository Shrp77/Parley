namespace Parley.Core;

/// <summary>
/// How long the message log keeps what it carried, per SPEC.md §9.4.
/// </summary>
/// <param name="IdlePeriod">
/// How long a conversation whose exchanges have all closed must stay silent before its
/// messages are prunable.
/// </param>
/// <param name="MaximumAge">The longest any message survives, whatever the idle rule concludes.</param>
/// <remarks>
/// <para>
/// This is the dial §9.4 describes, and it is a dial rather than a switch on purpose: there is
/// no retain-forever setting, because a permanent corner of the log would be the context store
/// §16 says Parley is not. An operator who wants to keep a discussion exports it, and the export
/// is a file they own outside this policy entirely.
/// </para>
/// <para>
/// Unlike <see cref="ExchangePolicy"/> these numbers are consulted live rather than stamped onto
/// a row, so changing one reaches backwards over everything already stored. That is the right
/// way round here and the wrong way round there: an exchange must reproduce its own behaviour
/// from its own row, while retention is a statement about the file as it stands now.
/// </para>
/// <para>
/// The floor of §9.4 is deliberately not configurable and so is not here. Its two absolute
/// clauses — a live exchange's messages, and anything still inside the deduplication window —
/// are properties of the mechanisms they protect rather than of a deployment's taste, and an
/// operator able to set them to nothing could break deduplication by tightening retention.
/// </para>
/// </remarks>
public sealed record RetentionPolicy(TimeSpan IdlePeriod, TimeSpan MaximumAge)
{
    /// <summary>
    /// The policy applied when a deployment configures none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retention runs by default, and that is a decision rather than an oversight. §9.4 opens by
    /// saying an unbounded log is something the design explicitly is not, so a policy that only
    /// applied once somebody configured it would make every unconfigured deployment the thing
    /// the section forbids — and unconfigured is what most deployments are.
    /// </para>
    /// <para>
    /// Three days and thirty days are starting estimates in the spirit of
    /// <see cref="ExchangePolicy.Default"/>, chosen against what the log is for rather than
    /// against sentiment about keeping records. Telemetry (§15) is computed from exchange rows,
    /// which retention does not touch, so the text only needs to outlive an operator's interest
    /// in reading a specific discussion back.
    /// </para>
    /// </remarks>
    public static RetentionPolicy Default { get; } = new(
        IdlePeriod: TimeSpan.FromDays(3),
        MaximumAge: TimeSpan.FromDays(30));

    /// <summary>
    /// Throws if the configuration is unusable.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A period is not positive, or the age cap is shorter than the idle period.
    /// </exception>
    /// <remarks>
    /// A cap below the idle period is refused because it makes the idle rule unreachable: every
    /// message would age out before a conversation could ever be judged finished, so the rule
    /// §9.4 spends four paragraphs on would silently never fire. A configuration whose stated
    /// behaviour cannot occur is worth refusing at start-up rather than discovering from
    /// telemetry.
    /// </remarks>
    public void Validate()
    {
        if (IdlePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(IdlePeriod), IdlePeriod, "The idle period must be positive.");
        }

        if (MaximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumAge), MaximumAge, "The maximum age must be positive.");
        }

        if (MaximumAge < IdlePeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumAge),
                MaximumAge,
                "The maximum age is shorter than the idle period, so the idle rule of SPEC.md " +
                "§9.4 could never fire: everything would age out before a conversation could be " +
                "judged finished.");
        }
    }
}
