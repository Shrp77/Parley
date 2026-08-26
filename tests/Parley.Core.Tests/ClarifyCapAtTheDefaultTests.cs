using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The clarify counter at the default the deployment actually runs, rather than at the small
/// caps the transition tests use.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of this rule sets the cap to 1 or 2, which is the right way to test a
/// boundary and leaves one thing unexercised: the number the default supplies. The two are the
/// same comparison and there is no honest argument that 200 behaves differently — but that is
/// reasoning, and the rule here is to probe. A default that admitted 199 or 201 would be a
/// silent off-by-one in the only number an unconfigured deployment ever uses.
/// </para>
/// <para>
/// Driven from <see cref="ExchangePolicy.Default"/> rather than the literal 200, so lowering
/// the default moves this test with it instead of leaving a test asserting a number nothing
/// uses any more.
/// </para>
/// </remarks>
public sealed class ClarifyCapAtTheDefaultTests
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static int Cap => ExchangePolicy.Default.ClarifyDepthCap;

    [Fact]
    public void The_default_admits_exactly_its_own_number_of_requests()
    {
        var exchange = Exchange.Open(1, Orders, Billing, T0, ExchangePolicy.Default);

        // A request from the answerer, then the asker's reply, repeated. Turn-taking is what
        // makes the reply mandatory: OwedBy alternates, so the answerer cannot ask twice
        // running, and this is the shape §5.3's "each request draws a reply" describes.
        for (var i = 0; i < Cap; i++)
        {
            Assert.True(exchange.Clarify(Billing, T0.AddMinutes(i)).Succeeded, $"request {i + 1}");
            Assert.True(exchange.Clarify(Orders, T0.AddMinutes(i)).Succeeded, $"reply {i + 1}");
        }

        Assert.Equal(Cap, exchange.ClarifyDepth);

        var overflow = exchange.Clarify(Billing, T0.AddHours(1));

        Assert.False(overflow.Succeeded);
        Assert.Equal(TransitionError.ClarifyDepthExceeded, overflow.Error);
    }

    [Fact]
    public void The_askers_replies_are_not_charged_to_the_counter()
    {
        // The asymmetry §5.3 now records, and the reason raising the cap is not purely
        // self-inflicted: the counter is stamped from the asker's policy, but only the
        // answerer's requests spend it. If replies were charged, the depth below would be
        // double and the exchange would refuse halfway through.
        var exchange = Exchange.Open(1, Orders, Billing, T0, ExchangePolicy.Default);

        for (var i = 0; i < 10; i++)
        {
            exchange.Clarify(Billing, T0.AddMinutes(i));
            exchange.Clarify(Orders, T0.AddMinutes(i));
        }

        Assert.Equal(10, exchange.ClarifyDepth);
    }

    [Fact]
    public void An_exchange_keeps_the_cap_it_was_admitted_under()
    {
        // Policy is stamped at admission and never read again, so an operator lowering the
        // default cannot reach back and refuse clarifications on work already in flight. That
        // is the property that makes this a policy rather than a settings object, and the one
        // that stops a change to the default from being retroactive.
        var strict = Exchange.Open(1, Orders, Billing, T0, ExchangePolicy.Default with { ClarifyDepthCap = 2 });

        Assert.Equal(2, strict.ClarifyDepthCap);
        Assert.NotEqual(Cap, strict.ClarifyDepthCap);

        strict.Clarify(Billing, T0);
        strict.Clarify(Orders, T0);
        strict.Clarify(Billing, T0);
        strict.Clarify(Orders, T0);

        Assert.Equal(TransitionError.ClarifyDepthExceeded, strict.Clarify(Billing, T0).Error);
    }
}
