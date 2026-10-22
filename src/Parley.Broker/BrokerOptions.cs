using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// How a broker runs: the terms exchanges are admitted under, what a restart wakes, and retention.
/// </summary>
/// <param name="ExchangePolicy">The terms stamped on every exchange at admission (§7.1).</param>
/// <param name="ResumePolicy">Whether a restart wakes agents for outstanding work (§14).</param>
/// <param name="ResumeGate">Asked before any agent is woken for outstanding work (§14).</param>
/// <param name="Retention">What the log is pruned to (§9.4).</param>
/// <param name="RetentionInterval">How often retention runs.</param>
/// <remarks>
/// Operator configuration, resolved before any agent-facing path is reached. Nothing here is a parameter
/// an agent can supply.
/// </remarks>
public sealed record BrokerOptions(
    ExchangePolicy ExchangePolicy,
    ResumePolicy ResumePolicy,
    IResumeGate ResumeGate,
    RetentionPolicy Retention,
    TimeSpan RetentionInterval)
{
    /// <summary>The retention interval when none is configured.</summary>
    /// <remarks>
    /// Hourly, because retention is measured in days and its pass walks the whole message log.
    /// </remarks>
    public static TimeSpan DefaultRetentionInterval { get; } = TimeSpan.FromHours(1);

    /// <summary>The options when a deployment configures none.</summary>
    /// <remarks>
    /// <see cref="ResumePolicy.Never"/>, the safe default for interactive use (§14): the surprising outcome
    /// is work resuming that somebody stopped on purpose.
    /// </remarks>
    public static BrokerOptions Default { get; } = new(
        Core.ExchangePolicy.Default,
        Core.ResumePolicy.Never,
        AlwaysResume.Instance,
        RetentionPolicy.Default,
        DefaultRetentionInterval);

    /// <summary>Throws if any value is unusable.</summary>
    /// <exception cref="ArgumentNullException">A required value is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ExchangePolicy);
        ArgumentNullException.ThrowIfNull(ResumeGate);
        ArgumentNullException.ThrowIfNull(Retention);

        ExchangePolicy.Validate();
        Retention.Validate();

        if (!Enum.IsDefined(ResumePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(ResumePolicy), ResumePolicy, "Not a defined resume policy.");
        }

        // Zero would sweep on every tick, silently.
        if (RetentionInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetentionInterval), RetentionInterval, "The retention sweep interval must be positive.");
        }
    }
}
