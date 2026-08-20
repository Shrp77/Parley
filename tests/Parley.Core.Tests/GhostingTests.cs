using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The terminal state an orphan would otherwise lack.
/// </summary>
/// <remarks>
/// Without ghosting a <c>never</c> resume policy retains orphans nothing ever clears, so
/// SPEC.md §9.4's age cap is not absolute — the gap that section used to concede. These pin
/// the rule that closes it, and in particular the clock, which is the part with a decision in
/// it rather than an obvious answer.
/// </remarks>
public sealed class GhostingTests
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delivery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Working = TimeSpan.FromHours(4);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(3);

    private static Exchange Orphaned(DateTimeOffset at)
    {
        var exchange = Exchange.Open(1, Orders, Billing, T0, Delivery, Working, Grace);
        Assert.True(exchange.Orphan(at).Succeeded);
        return exchange;
    }

    [Fact]
    public void Ghosting_closes_the_exchange_and_says_why()
    {
        var exchange = Orphaned(T0 + Delivery);

        Assert.True(exchange.Ghost(T0 + Delivery + Grace).Succeeded);

        Assert.Equal(ExchangeState.Closed, exchange.State);
        Assert.Equal(Resolution.Ghosted, exchange.Resolution);
        Assert.Equal(T0 + Delivery + Grace, exchange.ClosedUtc);
    }

    [Fact]
    public void Only_an_orphan_may_be_ghosted()
    {
        // An open exchange is still being waited on, and a closed one already said why it
        // closed. Ghosting either would overwrite a real outcome with an infrastructure one.
        var open = Exchange.Open(1, Orders, Billing, T0, Delivery, Working, Grace);
        Assert.Equal(TransitionError.ExchangeNotOrphaned, open.Ghost(T0).Error);

        Assert.True(open.Answer(Billing, T0).Succeeded);
        Assert.Equal(TransitionError.ExchangeNotOrphaned, open.Ghost(T0).Error);
        Assert.Equal(Resolution.Answered, open.Resolution);
    }

    [Fact]
    public void The_grace_period_must_elapse()
    {
        var exchange = Orphaned(T0 + Delivery);
        var orphanedAt = T0 + Delivery;

        Assert.False(exchange.IsGhostableAt(orphanedAt + Grace - TimeSpan.FromTicks(1), T0));
        Assert.True(exchange.IsGhostableAt(orphanedAt + Grace, T0));
    }

    [Fact]
    public void A_broker_that_was_down_gives_every_orphan_a_fresh_window()
    {
        // The decision this rule exists for. An exchange orphaned at 22:00 with a three hour
        // grace period, on a broker that died at 22:05 and returned at 09:00, has had its
        // window elapse while nothing could possibly have resumed it. A wall-clock rule would
        // ghost it within a second of start-up, having never once attempted delivery.
        var orphanedAt = new DateTimeOffset(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);
        var brokerStart = new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero);

        var exchange = Exchange.Open(1, Orders, Billing, orphanedAt.AddMinutes(-30),
            Delivery, Working, Grace);
        Assert.True(exchange.Orphan(orphanedAt).Succeeded);

        Assert.False(exchange.IsGhostableAt(brokerStart, brokerStart));
        Assert.False(exchange.IsGhostableAt(brokerStart.AddHours(2), brokerStart));
        Assert.True(exchange.IsGhostableAt(brokerStart + Grace, brokerStart));
    }

    [Fact]
    public void In_steady_running_the_two_clocks_agree()
    {
        // Broker start is in the past for everything orphaned since, so taking the later of the
        // two changes nothing while the broker stays up. If this were not so, the rule would be
        // paying for the outage case on every ordinary sweep.
        var brokerStart = T0;
        var exchange = Orphaned(T0 + Delivery);
        var orphanedAt = T0 + Delivery;

        Assert.False(exchange.IsGhostableAt(orphanedAt + Grace - TimeSpan.FromMinutes(1), brokerStart));
        Assert.True(exchange.IsGhostableAt(orphanedAt + Grace, brokerStart));
    }

    [Fact]
    public void An_exchange_that_is_not_orphaned_is_never_ghostable()
    {
        var open = Exchange.Open(1, Orders, Billing, T0, Delivery, Working, Grace);

        Assert.False(open.IsGhostableAt(T0.AddYears(1), T0));
    }

    [Fact]
    public void A_resumed_orphan_is_no_longer_ghostable()
    {
        // Resumption is the outcome ghosting exists to wait for, so it must clear the clock
        // rather than leaving a sweep to close an exchange that has just come back to life.
        var exchange = Orphaned(T0 + Delivery);
        Assert.True(exchange.Resume(T0.AddHours(1)).Succeeded);

        Assert.False(exchange.IsGhostableAt(T0.AddDays(1), T0));
    }

    [Fact]
    public void Resuming_restarts_the_delivery_window_and_forgets_delivery()
    {
        // A replay marks the question undelivered again (SPEC.md §14), so the exchange is
        // waiting for pickup rather than for an answer. Granting the working window here would
        // give an agent that never returned four hours of silence instead of fifteen minutes.
        var exchange = Exchange.Open(1, Orders, Billing, T0, Delivery, Working, Grace);
        Assert.True(exchange.Deliver(T0.AddMinutes(1)).Succeeded);
        Assert.Equal(T0.AddMinutes(1) + Working, exchange.ExpiresUtc);

        Assert.True(exchange.Orphan(T0.AddHours(5)).Succeeded);
        var resumedAt = T0.AddHours(6);
        Assert.True(exchange.Resume(resumedAt).Succeeded);

        Assert.Null(exchange.DeliveredUtc);
        Assert.Equal(resumedAt + Delivery, exchange.ExpiresUtc);
    }

    [Fact]
    public void An_answer_cannot_claim_to_be_a_ghost()
    {
        // A ghost asserts nobody answered. The schema refuses this too; refusing here means the
        // caller learns what it did rather than meeting a constraint violation further down.
        var exchange = Exchange.Open(1, Orders, Billing, T0, Delivery, Working, Grace);

        var result = exchange.Answer(Billing, T0, Resolution.Ghosted);

        Assert.Equal(TransitionError.GhostIsNotAnAnswer, result.Error);
        Assert.Equal(ExchangeState.Open, exchange.State);
        Assert.Null(exchange.Resolution);
    }
}
