using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What a deployment decides, as opposed to what an exchange carries.
/// </summary>
/// <param name="ExchangePolicy">The terms new exchanges are admitted under.</param>
/// <param name="ResumePolicy">What a restart does to the exchanges it interrupted.</param>
/// <param name="ResumeGate">Consulted before each replay under <see cref="Core.ResumePolicy.Always"/>.</param>
/// <param name="Retention">How long the message log keeps what it carried. See SPEC.md §9.4.</param>
/// <param name="RetentionInterval">How often <see cref="Broker.TickAsync"/> applies it.</param>
/// <remarks>
/// <para>
/// The division here is not arbitrary. <see cref="ExchangePolicy"/> is stamped onto every
/// exchange at admission and never read again, so changing it affects exchanges opened
/// afterwards and never reaches back. The others are consulted live, at restart, at replay and
/// on the sweep, so changing them takes effect immediately and applies to work already in
/// flight.
/// </para>
/// <para>
/// <see cref="Retention"/> reaches furthest of all, and is the one worth pausing over.
/// Everything else here decides what happens next; retention decides what is still there, so
/// shortening it removes text that was written under the old setting. That is intended — §9.4
/// makes it a statement about the file as it stands rather than a term each message was
/// admitted under — but it means a project whose servers are configured differently converges
/// on the shortest policy any of them holds, since any server may prune.
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
    RetentionPolicy Retention,
    TimeSpan RetentionInterval)
{
    /// <summary>
    /// How often the sweep applies the retention policy, when a deployment specifies nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retention is not a deadline, which is what separates it from everything else the tick
    /// does. The reaper enforces windows measured in minutes and must run at that cadence or a
    /// peer waits past a deadline that has already passed. The idle period of SPEC.md §9.4 is
    /// measured in days, so a sweep every minute spends a full pass over the message log to
    /// discover something that could not have changed since the last one.
    /// </para>
    /// <para>
    /// It is not free to discover it. The pass runs under the same <c>IMMEDIATE</c> lock
    /// admission takes, so its cost is paid by every question in the deployment: measured
    /// against a log of twenty thousand messages it was eighty milliseconds, and against two
    /// hundred thousand it was a second — on every tick, of every server, whether or not
    /// anything was due. An hour turns that into noise without letting anything outlive its
    /// policy by a margin that matters against a floor of days.
    /// </para>
    /// <para>
    /// The first tick after a start prunes regardless, so a deployment whose sessions are short
    /// still forgets. That also makes the cost visible where it is cheapest to notice.
    /// </para>
    /// </remarks>
    public static TimeSpan DefaultRetentionInterval { get; } = TimeSpan.FromHours(1);

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
        RetentionPolicy.Default,
        DefaultRetentionInterval);

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
        ArgumentNullException.ThrowIfNull(Retention);

        ExchangePolicy.Validate();
        Retention.Validate();

        // An age cap shorter than an exchange's own lifetime says "forget the question before
        // the answer is due". Nothing is corrupted by it -- the floor of SPEC.md 9.4 holds a
        // live exchange's messages absolutely, so the cap simply never reaches them -- but it
        // is a policy whose stated effect cannot occur, and the two numbers live in different
        // objects, so nothing else is positioned to notice.
        var longestExchange = ExchangePolicy.DeliveryWindow + ExchangePolicy.WorkingWindow +
                              ExchangePolicy.GracePeriod;

        if (Retention.MaximumAge < longestExchange)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Retention),
                Retention.MaximumAge,
                $"The retention age cap ({Retention.MaximumAge}) is shorter than the longest an " +
                $"exchange may live under this exchange policy ({longestExchange}), so it would " +
                "expire a question before its own answer was due.");
        }

        if (!Enum.IsDefined(ResumePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ResumePolicy), ResumePolicy, "Not a defined resume policy.");
        }


        // Zero would restore the every-tick sweep this interval exists to replace, and would do
        // it silently -- the deployment would work, and pay a full pass over the message log for
        // every question anybody asked.
        if (RetentionInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetentionInterval), RetentionInterval,
                "The retention sweep interval must be positive.");
        }
    }
}

