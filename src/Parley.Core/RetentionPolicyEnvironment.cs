namespace Parley.Core;

/// <summary>
/// Where a deployment states its retention policy.
/// </summary>
/// <remarks>
/// <para>
/// Reading configuration is not this project's usual business for a domain type, and this is
/// deliberate rather than an exception made quietly. §9.4 says the policy is a property of the
/// file rather than of any one process, and two passes apply it: the broker's hourly retention
/// pass, and a person's <c>prune</c>. If each parsed its own variables, a deployment could
/// have them disagree about which numbers were in force while both reported success — and
/// retention is the one setting whose mistakes are unrecoverable, because what it removes is
/// gone.
/// </para>
/// <para>
/// So the names and the parsing live in one place, and it is here rather than in either host
/// because neither host references the other. Nothing about it is I/O: the source of variables
/// is a delegate, which is also what lets it be tested without mutating the process
/// environment — a real concern, since the test host runs classes in parallel and a test that
/// set a variable would set it for everything else running at that moment.
/// </para>
/// </remarks>
public static class RetentionPolicyEnvironment
{
    /// <summary>The variable naming the idle period, in hours.</summary>
    public const string IdleVariable = "PARLEY_RETENTION_IDLE_HOURS";

    /// <summary>The variable naming the maximum age, in hours.</summary>
    public const string MaximumAgeVariable = "PARLEY_RETENTION_MAX_AGE_HOURS";

    /// <summary>
    /// Reads the retention policy, falling back to <see cref="RetentionPolicy.Default"/>.
    /// </summary>
    /// <param name="read">Returns a variable's value, or <see langword="null"/> if it is unset.</param>
    /// <returns>The policy this deployment states.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A variable carries something that is not a positive whole number of hours, or more hours
    /// than a retention period can hold, or the two together describe a policy that cannot be
    /// applied.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Hours for both, rather than days for the cap and hours for the idle period. Mixed units
    /// across two variables that are compared against each other is how a deployment ends up
    /// with an age cap of thirty <em>hours</em>, and the unit is in each name so that neither
    /// has to be looked up.
    /// </para>
    /// <para>
    /// An unreadable value is refused rather than ignored. An operator who set this meant
    /// something by it, and falling back silently leaves them a log being pruned on numbers they
    /// did not choose and cannot see — which for this setting means text already deleted by the
    /// time anybody checks.
    /// </para>
    /// </remarks>
    public static RetentionPolicy Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var policy = new RetentionPolicy(
            Hours(read, IdleVariable, RetentionPolicy.Default.IdlePeriod),
            Hours(read, MaximumAgeVariable, RetentionPolicy.Default.MaximumAge));

        try
        {
            policy.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Rethrown as a configuration failure rather than surfacing as an argument error.
            // Nobody passed an argument: two environment variables disagree, and the message
            // that reaches whoever wrote them should name them.
            throw new InvalidOperationException(
                $"{IdleVariable} and {MaximumAgeVariable} describe a policy that cannot be " +
                $"applied. {ex.Message}", ex);
        }

        return policy;
    }

    /// <summary>Reads a duration stated in whole hours, or falls back.</summary>
    /// <param name="read">The source of variables.</param>
    /// <param name="name">The variable's name.</param>
    /// <param name="fallback">What to use when it is unset or blank.</param>
    /// <returns>The configured duration.</returns>
    /// <exception cref="InvalidOperationException">
    /// The value is not a positive whole number of hours, or is more hours than a retention period
    /// can hold.
    /// </exception>
    private static TimeSpan Hours(Func<string, string?> read, string name, TimeSpan fallback)
    {
        var configured = read(name);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }

        if (!int.TryParse(configured, out var hours) || hours <= 0)
        {
            throw new InvalidOperationException(
                $"{name} is '{configured}', which is not a positive whole number of hours.");
        }

        // Refused here, naming the variable, rather than overflowing TimeSpan with a message that names neither.
        if (hours > TimeSpan.MaxValue.TotalHours / 2)
        {
            throw new InvalidOperationException(
                $"{name} is '{configured}', which is more hours than a retention period can hold.");
        }

        return TimeSpan.FromHours(hours);
    }
}
