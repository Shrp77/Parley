using Xunit;

namespace Parley.Core.Tests;

public sealed class ExchangeTests
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delivery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Working = TimeSpan.FromHours(4);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(3);

    private static readonly ExchangePolicy Policy = ExchangePolicy.Default with
    {
        DeliveryWindow = Delivery,
        WorkingWindow = Working,
        GracePeriod = Grace
    };

    private static Exchange OpenExchange(int clarifyCap = 3) =>
        Exchange.Open(1, Orders, Billing, T0, Policy with { ClarifyDepthCap = clarifyCap });

    [Fact]
    public void Opens_with_answerer_owing()
    {
        var exchange = OpenExchange();

        Assert.Equal(ExchangeState.Open, exchange.State);
        Assert.Equal(Billing, exchange.OwedBy);
        Assert.Equal(T0 + Delivery, exchange.ExpiresUtc);
    }

    [Fact]
    public void Asker_cannot_answer_its_own_question()
    {
        var exchange = OpenExchange();

        var result = exchange.Answer(Orders, T0);

        Assert.False(result.Succeeded);
        Assert.Equal(TransitionError.OnlyAnswererMayAnswer, result.Error);
    }

    [Fact]
    public void Non_participant_is_rejected()
    {
        var exchange = OpenExchange();

        var result = exchange.Clarify(Inventory, T0);

        Assert.False(result.Succeeded);
        Assert.Equal(TransitionError.NotAParticipant, result.Error);
    }

    [Fact]
    public void Answer_closes_and_releases()
    {
        var exchange = OpenExchange();

        Assert.True(exchange.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.Equal(ExchangeState.Closed, exchange.State);
        Assert.Equal(T0.AddMinutes(1), exchange.ClosedUtc);
    }

    [Fact]
    public void Closed_exchange_accepts_nothing_further()
    {
        var exchange = OpenExchange();
        exchange.Answer(Billing, T0);

        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.Clarify(Billing, T0).Error);
        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.Answer(Billing, T0).Error);
    }

    [Fact]
    public void Clarify_flips_the_turn_and_resets_the_ttl()
    {
        var exchange = OpenExchange();
        var later = T0.AddMinutes(5);

        Assert.True(exchange.Clarify(Billing, later).Succeeded);

        Assert.Equal(Orders, exchange.OwedBy);
        Assert.Equal(later + Working, exchange.ExpiresUtc);
    }

    [Fact]
    public void Clarify_out_of_turn_is_rejected()
    {
        var exchange = OpenExchange();

        // Billing owes the answer; Orders has nothing to say yet.
        var result = exchange.Clarify(Orders, T0);

        Assert.False(result.Succeeded);
        Assert.Equal(TransitionError.NotYourTurn, result.Error);
    }

    [Fact]
    public void Depth_counts_requests_but_not_replies()
    {
        var exchange = OpenExchange();

        exchange.Clarify(Billing, T0);   // request  -> depth 1
        Assert.Equal(1, exchange.ClarifyDepth);

        exchange.Clarify(Orders, T0);    // reply    -> unchanged
        Assert.Equal(1, exchange.ClarifyDepth);

        exchange.Clarify(Billing, T0);   // request  -> depth 2
        Assert.Equal(2, exchange.ClarifyDepth);
    }

    [Fact]
    public void Clarify_cap_forces_an_unknown_answer()
    {
        var exchange = OpenExchange(clarifyCap: 1);

        exchange.Clarify(Billing, T0);
        exchange.Clarify(Orders, T0);

        var blocked = exchange.Clarify(Billing, T0);
        Assert.Equal(TransitionError.ClarifyDepthExceeded, blocked.Error);

        // The broker's escape hatch: the answerer does not hold the turn at this point,
        // so ForceUnknown must bypass the turn check.
        Assert.True(exchange.ForceUnknown(T0).Succeeded);
        Assert.Equal(ExchangeState.Closed, exchange.State);
    }

    [Fact]
    public void Expiry_is_reported_only_while_open()
    {
        var exchange = OpenExchange();
        var past = T0 + Delivery + TimeSpan.FromSeconds(1);

        Assert.True(exchange.IsExpiredAt(past));

        exchange.Answer(Billing, T0);
        Assert.False(exchange.IsExpiredAt(past));
    }

    [Fact]
    public void Orphan_then_resume_preserves_clarify_depth()
    {
        var exchange = OpenExchange();
        exchange.Clarify(Billing, T0);

        Assert.True(exchange.Orphan(T0.AddMinutes(20)).Succeeded);
        Assert.Equal(ExchangeState.Orphaned, exchange.State);

        var resumedAt = T0.AddHours(2);
        Assert.True(exchange.Resume(resumedAt).Succeeded);

        Assert.Equal(ExchangeState.Open, exchange.State);
        // The delivery window, not the working one: a replay is waiting for pickup again.
        Assert.Equal(resumedAt + Delivery, exchange.ExpiresUtc);

        // A restart is not a fresh clarification budget.
        Assert.Equal(1, exchange.ClarifyDepth);
    }

    [Fact]
    public void Resume_requires_an_orphaned_exchange()
    {
        var exchange = OpenExchange();

        Assert.Equal(TransitionError.ExchangeNotOrphaned, exchange.Resume(T0).Error);
    }

    [Fact]
    public void Self_questioning_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Exchange.Open(1, Orders, Orders, T0, Policy));
    }
}
