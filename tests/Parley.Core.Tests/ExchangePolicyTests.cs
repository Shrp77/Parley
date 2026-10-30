using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The terms an exchange is admitted under.
/// </summary>
/// <remarks>
/// Validation is duplicated between this type and the schema on purpose. The schema is what
/// survives a broker bug and is therefore the real guarantee; this is what makes a
/// misconfigured deployment fail at start-up naming the field, rather than on the first
/// request with a constraint violation raised several layers below the mistake. These pin the
/// two to the same bounds, because a check that is merely close to the constraint it mirrors is
/// worse than none: it rejects things storage would have accepted.
/// </remarks>
public sealed class ExchangePolicyTests
{
    [Fact]
    public void The_default_is_valid()
    {
        ExchangePolicy.Default.Validate();
    }

    [Fact]
    public void The_default_bounds_open_requests_not_at_all()
    {
        // SPEC.md §7.1: there is no request cap; the broker stamps it off. Asserted on the
        // property rather than on the number, because what a caller needs to know is whether a
        // bound applies — and the number the sentinel happens to be is storage's business.
        Assert.False(ExchangePolicy.Default.BoundsRequests);
        Assert.Equal(ExchangePolicy.Unbounded, ExchangePolicy.Default.OpenPairCap);
    }

    [Fact]
    public void One_below_the_sentinel_is_a_cap_like_any_other()
    {
        // The band is not dead above the numbers anybody would choose: a deployment that has
        // observed a flood may set 49 and have storage hold it. If this ever fails, the sentinel
        // has eaten a legal cap.
        var bounded = ExchangePolicy.Default with { OpenPairCap = ExchangePolicy.Unbounded - 1 };

        bounded.Validate();
        Assert.True(bounded.BoundsRequests);
    }

    [Fact]
    public void The_sentinel_is_inside_what_storage_accepts()
    {
        // It is the ceiling of the CHECK rather than a value beside it, so a policy asking for
        // no cap is storable. A sentinel the schema refused would fail at the first admission.
        Assert.Equal(ExchangePolicy.MaximumOpenPairCap, ExchangePolicy.Unbounded);

        (ExchangePolicy.Default with { OpenPairCap = ExchangePolicy.Unbounded }).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ExchangePolicy.MaximumOpenPairCap + 1)]
    public void An_open_pair_cap_outside_the_band_is_rejected(int cap)
    {
        // A cap of zero admits nothing at all; one above the band disables the rule while
        // leaving every constraint satisfied and nothing to notice.
        var policy = ExchangePolicy.Default with { OpenPairCap = cap };

        Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);
    }

    [Fact]
    public void A_non_positive_threshold_is_rejected()
    {
        // A threshold of zero flags every Request the instant it is sent, which tells the person
        // running the deployment nothing.
        Assert.Throws<ArgumentOutOfRangeException>(
            (ExchangePolicy.Default with { UncollectedAfter = TimeSpan.Zero }).Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            (ExchangePolicy.Default with { StalledAfter = TimeSpan.Zero }).Validate);
    }

    [Fact]
    public void A_negative_clarify_depth_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            (ExchangePolicy.Default with { ClarifyDepthCap = -1 }).Validate);
    }

    [Fact]
    public void A_clarify_depth_cap_of_zero_is_permitted()
    {
        // Zero means no clarification at all: send a Result from what you were given, blocked if need be.
        // A deployment that finds clarification unproductive should be able to switch it off
        // without the type second-guessing the choice.
        (ExchangePolicy.Default with { ClarifyDepthCap = 0 }).Validate();
    }

    [Fact]
    public void A_clarify_cap_above_the_band_is_rejected_unless_it_is_the_no_cap_value()
    {
        // The same bound the storage triggers hold, checked here so a misconfigured deployment fails
        // at start-up naming the field. The one value past the band is the one that means "off".
        (ExchangePolicy.Default with { ClarifyDepthCap = ExchangePolicy.MaximumClarifyDepthCap }).Validate();
        (ExchangePolicy.Default with { ClarifyDepthCap = ExchangePolicy.UnboundedClarifications }).Validate();

        Assert.Throws<ArgumentOutOfRangeException>(
            (ExchangePolicy.Default with { ClarifyDepthCap = ExchangePolicy.MaximumClarifyDepthCap + 1 }).Validate);
    }

    [Fact]
    public void A_named_policy_overrides_only_what_it_names()
    {
        // The cascade a deployment configures: an agent that runs a full end-to-end suite
        // needs a longer stalled threshold and nothing else about it changes.
        var slowRepo = ExchangePolicy.Default with { StalledAfter = TimeSpan.FromHours(12) };

        Assert.Equal(TimeSpan.FromHours(12), slowRepo.StalledAfter);
        Assert.Equal(ExchangePolicy.Default.UncollectedAfter, slowRepo.UncollectedAfter);
        Assert.Equal(ExchangePolicy.Default.ClarifyDepthCap, slowRepo.ClarifyDepthCap);
        Assert.Equal(ExchangePolicy.Default.OpenPairCap, slowRepo.OpenPairCap);
    }

    [Fact]
    public void An_exchange_opened_without_a_policy_takes_the_default()
    {
        var orders = AgentName.Create("orders@test");
        var billing = AgentName.Create("billing@test");
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var exchange = Exchange.Open(1, orders, billing, t0);

        Assert.Equal(ExchangePolicy.Default.UncollectedAfter, exchange.UncollectedAfter);
        Assert.Equal(ExchangePolicy.Default.StalledAfter, exchange.StalledAfter);
        Assert.Equal(ExchangePolicy.Default.ClarifyDepthCap, exchange.ClarifyDepthCap);
    }

    [Fact]
    public void An_exchange_refuses_a_policy_storage_would_not_accept()
    {
        // Caught at the domain boundary rather than three layers down at the INSERT.
        var orders = AgentName.Create("orders@test");
        var billing = AgentName.Create("billing@test");
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => Exchange.Open(
            1, orders, billing, t0, ExchangePolicy.Default with { StalledAfter = TimeSpan.Zero }));
    }
}
