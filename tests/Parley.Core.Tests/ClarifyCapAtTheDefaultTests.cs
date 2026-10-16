using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The clarify counter at the default the deployment actually runs, rather than at the small
/// caps the transition tests use.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default is no cap, and it was 200.</b> SPEC.md principle 2.8 says nothing cuts a line of
/// work off before its participants are finished, and clarification is how two of them converge,
/// so no cap is set by default, as with the pair cap. These check that the default
/// really admits more than any number a cap could be, and that a deployment which sets a cap
/// still has it held.
/// </para>
/// <para>
/// Driven from <see cref="ExchangePolicy.Default"/> rather than a literal, so a later change to the
/// default moves this test with it instead of leaving a test asserting a number nothing uses.
/// </para>
/// </remarks>
public sealed class ClarifyCapAtTheDefaultTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>More rounds than the storage band allows a real cap to be.</summary>
    /// <remarks>
    /// Past <see cref="ExchangePolicy.MaximumClarifyDepthCap"/> rather than past the old default of
    /// 200, so a default that was merely a large cap inside the band would fail here too.
    /// </remarks>
    private const int PastAnyCap = ExchangePolicy.MaximumClarifyDepthCap + 500;

    [Fact]
    public void The_default_refuses_no_clarification()
    {
        var exchange = Exchange.Open(1, Orders, Billing, T0, ExchangePolicy.Default);

        // A request from the receiver, then the requester's reply, repeated. Turn-taking makes the
        // reply mandatory: OwedBy alternates, so the receiver cannot ask twice running.
        for (var i = 0; i < PastAnyCap; i++)
        {
            Assert.True(exchange.Clarify(Billing, T0.AddMinutes(i)).Succeeded, $"request {i + 1}");
            Assert.True(exchange.Clarify(Orders, T0.AddMinutes(i)).Succeeded, $"reply {i + 1}");
        }

        Assert.Equal(PastAnyCap, exchange.ClarifyDepth);
    }

    [Fact]
    public void The_default_is_the_no_cap_value_and_not_a_large_cap()
    {
        Assert.False(ExchangePolicy.Default.BoundsClarifications);
        Assert.Equal(ExchangePolicy.UnboundedClarifications, ExchangePolicy.Default.ClarifyDepthCap);

        // The top of the band is a real cap. If the sentinel ever ate it, a deployment asking for
        // a thousand would silently get none.
        Assert.True((ExchangePolicy.Default with { ClarifyDepthCap = ExchangePolicy.MaximumClarifyDepthCap }).BoundsClarifications);
    }

    [Fact]
    public void The_requesters_replies_are_not_charged_to_the_counter()
    {
        // Only the receiver's requests spend it. If replies were charged, the depth below would be
        // double, and a deployment's cap would refuse halfway through.
        var exchange = Exchange.Open(1, Orders, Billing, T0, ExchangePolicy.Default);

        for (var i = 0; i < 10; i++)
        {
            exchange.Clarify(Billing, T0.AddMinutes(i));
            exchange.Clarify(Orders, T0.AddMinutes(i));
        }

        Assert.Equal(10, exchange.ClarifyDepth);
    }

    [Fact]
    public void A_cap_a_deployment_sets_is_still_held_at_its_own_number()
    {
        // Policy is stamped at admission and never read again, so the default changing cannot
        // reach back into work already in flight, and a deployment that set a cap keeps it.
        var strict = Exchange.Open(1, Orders, Billing, T0, ExchangePolicy.Default with { ClarifyDepthCap = 2 });

        Assert.Equal(2, strict.ClarifyDepthCap);

        strict.Clarify(Billing, T0);
        strict.Clarify(Orders, T0);
        strict.Clarify(Billing, T0);
        strict.Clarify(Orders, T0);

        Assert.Equal(TransitionError.ClarifyDepthExceeded, strict.Clarify(Billing, T0).Error);
    }
}
