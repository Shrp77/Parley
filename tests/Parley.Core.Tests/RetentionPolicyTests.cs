using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The dial of SPEC.md §9.4, and the two variables a deployment states it in.
/// </summary>
/// <remarks>
/// Reading the configuration lives in the domain because two binaries apply the same policy to
/// the same file — the agents' servers on every tick and the operator's <c>prune</c> — and
/// retention is the one setting whose mistakes cannot be undone. These pin that the names and
/// the parsing are shared, which is the whole reason it is not in either host.
/// </remarks>
public sealed class RetentionPolicyTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] set) =>
        name => set.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void The_default_keeps_a_month_and_calls_three_days_idle()
    {
        // Pinned because "unconfigured" is what most deployments are, and §9.4 says an unbounded
        // log is something the design explicitly is not — so a default of "keep everything"
        // would make every default deployment the thing the section forbids.
        Assert.Equal(TimeSpan.FromDays(3), RetentionPolicy.Default.IdlePeriod);
        Assert.Equal(TimeSpan.FromDays(30), RetentionPolicy.Default.MaximumAge);

        RetentionPolicy.Default.Validate();
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(3, 0)]
    [InlineData(3, -1)]
    public void A_period_that_is_not_positive_is_refused(int idleDays, int maximumDays)
    {
        var policy = new RetentionPolicy(
            TimeSpan.FromDays(idleDays), TimeSpan.FromDays(maximumDays));

        Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);
    }

    [Fact]
    public void An_age_cap_below_the_idle_period_is_refused()
    {
        // Not merely odd: it makes the idle rule unreachable, because everything would age out
        // before a conversation could ever be judged finished. A configuration whose stated
        // behaviour cannot occur is worth refusing where it is written down rather than
        // inferring from telemetry that four paragraphs of §9.4 never fired.
        var policy = new RetentionPolicy(TimeSpan.FromDays(30), TimeSpan.FromDays(3));

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);

        Assert.Contains("idle rule", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_periods_are_allowed()
    {
        // The boundary, and the right side of it. An age cap exactly at the idle period is
        // aggressive rather than incoherent: the idle rule still decides, and the cap agrees
        // with it.
        new RetentionPolicy(TimeSpan.FromDays(3), TimeSpan.FromDays(3)).Validate();
    }

    // ---- configuration ----

    [Fact]
    public void An_unconfigured_environment_yields_the_default()
    {
        Assert.Equal(RetentionPolicy.Default, RetentionPolicyEnvironment.Read(Environment()));
    }

    [Fact]
    public void Each_variable_can_be_set_alone()
    {
        // Independently defaulted, because an operator tightening the age cap has no reason to
        // restate an idle period they were happy with — and a variable that only works in pairs
        // is one somebody sets half of.
        var idleOnly = RetentionPolicyEnvironment.Read(
            Environment((RetentionPolicyEnvironment.IdleVariable, "12")));

        Assert.Equal(TimeSpan.FromHours(12), idleOnly.IdlePeriod);
        Assert.Equal(RetentionPolicy.Default.MaximumAge, idleOnly.MaximumAge);

        var capOnly = RetentionPolicyEnvironment.Read(
            Environment((RetentionPolicyEnvironment.MaximumAgeVariable, "2000")));

        Assert.Equal(RetentionPolicy.Default.IdlePeriod, capOnly.IdlePeriod);
        Assert.Equal(TimeSpan.FromHours(2000), capOnly.MaximumAge);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_is_the_same_as_unset(string configured)
    {
        Assert.Equal(
            RetentionPolicy.Default,
            RetentionPolicyEnvironment.Read(
                Environment((RetentionPolicyEnvironment.IdleVariable, configured))));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("2.5")]
    [InlineData("72 hours")]
    public void An_unreadable_value_is_refused_rather_than_ignored(string configured)
    {
        // Falling back silently would leave an operator with a log being pruned on numbers they
        // did not choose and cannot see — and for this setting, "cannot see" means the evidence
        // has already been deleted by the time anybody checks.
        var refusal = Assert.Throws<InvalidOperationException>(
            () => RetentionPolicyEnvironment.Read(
                Environment((RetentionPolicyEnvironment.IdleVariable, configured))));

        Assert.Contains(RetentionPolicyEnvironment.IdleVariable, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(configured, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_variables_that_contradict_each_other_name_both()
    {
        // The failure that is in neither value on its own. Whoever wrote them needs to be told
        // which pair disagrees, not handed an argument-out-of-range about a parameter nobody
        // passed.
        var refusal = Assert.Throws<InvalidOperationException>(
            () => RetentionPolicyEnvironment.Read(Environment(
                (RetentionPolicyEnvironment.IdleVariable, "720"),
                (RetentionPolicyEnvironment.MaximumAgeVariable, "24"))));

        Assert.Contains(RetentionPolicyEnvironment.IdleVariable, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(RetentionPolicyEnvironment.MaximumAgeVariable, refusal.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentOutOfRangeException>(refusal.InnerException);
    }

    [Fact]
    public void Reading_needs_a_source()
    {
        Assert.Throws<ArgumentNullException>(() => RetentionPolicyEnvironment.Read(null!));
    }
}
