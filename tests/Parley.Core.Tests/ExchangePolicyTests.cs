using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The terms an exchange is admitted under.
/// </summary>
/// <remarks>
/// Validation is duplicated between this type and the schema on purpose. The schema is what
/// survives a broker bug and is therefore the real guarantee; this is what makes a
/// misconfigured deployment fail at start-up naming the field, rather than on the first
/// question with a constraint violation raised several layers below the mistake. These pin the
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
    public void A_grace_period_of_zero_is_rejected()
    {
        // At zero an orphan is ghosted the instant it is created, so the resume policy never
        // runs and `always` becomes indistinguishable from `never`.
        var policy = ExchangePolicy.Default with { GracePeriod = TimeSpan.Zero };

        Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);
    }

    [Fact]
    public void A_grace_period_beyond_the_ceiling_is_rejected()
    {
        // At the top the exchange effectively never closes, restoring the unbounded retention
        // ghosting exists to remove.
        var policy = ExchangePolicy.Default with
        {
            GracePeriod = ExchangePolicy.MaximumGracePeriod + TimeSpan.FromSeconds(1)
        };

        Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);
    }

    [Fact]
    public void The_grace_period_bounds_are_inclusive()
    {
        (ExchangePolicy.Default with { GracePeriod = ExchangePolicy.MinimumGracePeriod }).Validate();
        (ExchangePolicy.Default with { GracePeriod = ExchangePolicy.MaximumGracePeriod }).Validate();
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
    public void A_non_positive_window_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            (ExchangePolicy.Default with { DeliveryWindow = TimeSpan.Zero }).Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            (ExchangePolicy.Default with { WorkingWindow = TimeSpan.Zero }).Validate);
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
        // Zero means no clarification at all: answer from what you were given or say unknown.
        // A deployment that finds clarification unproductive should be able to switch it off
        // without the type second-guessing the choice.
        (ExchangePolicy.Default with { ClarifyDepthCap = 0 }).Validate();
    }

    [Fact]
    public void A_named_policy_overrides_only_what_it_names()
    {
        // The cascade a deployment configures: an agent that runs a full end-to-end suite
        // needs a longer working window and nothing else about it changes.
        var slowRepo = ExchangePolicy.Default with { WorkingWindow = TimeSpan.FromHours(12) };

        Assert.Equal(TimeSpan.FromHours(12), slowRepo.WorkingWindow);
        Assert.Equal(ExchangePolicy.Default.DeliveryWindow, slowRepo.DeliveryWindow);
        Assert.Equal(ExchangePolicy.Default.GracePeriod, slowRepo.GracePeriod);
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

        Assert.Equal(ExchangePolicy.Default.DeliveryWindow, exchange.DeliveryWindow);
        Assert.Equal(ExchangePolicy.Default.WorkingWindow, exchange.WorkingWindow);
        Assert.Equal(ExchangePolicy.Default.GracePeriod, exchange.GracePeriod);
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
            1, orders, billing, t0, ExchangePolicy.Default with { GracePeriod = TimeSpan.Zero }));
    }
}
