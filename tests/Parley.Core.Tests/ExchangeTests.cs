using Xunit;

namespace Parley.Core.Tests;

[Unit]
public sealed class ExchangeTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Exchange OpenExchange(int clarifyCap = 3) =>
        Exchange.Open(1, Orders, Billing, T0, ExchangePolicy.Default with { ClarifyDepthCap = clarifyCap });

    [Fact]
    public void Opens_with_the_receiver_owing()
    {
        var exchange = OpenExchange();

        Assert.Equal(ExchangeState.Open, exchange.State);
        Assert.Equal(Billing, exchange.OwedBy);
        Assert.Equal(Orders, exchange.Waiting);
        Assert.Null(exchange.Outcome);
        Assert.Null(exchange.DeliveredUtc);
    }

    [Fact]
    public void The_requester_cannot_send_the_result()
    {
        var exchange = OpenExchange();

        var result = exchange.Close(Orders, T0, ExchangeOutcome.Complete);

        Assert.Equal(TransitionError.OnlyReceiverSendsResult, result.Error);
        Assert.Equal(ExchangeState.Open, exchange.State);
    }

    [Fact]
    public void A_non_participant_is_rejected()
    {
        var exchange = OpenExchange();

        Assert.Equal(TransitionError.NotAParticipant, exchange.Clarify(Inventory, T0).Error);
        Assert.Equal(TransitionError.NotAParticipant, exchange.Close(Inventory, T0, ExchangeOutcome.Complete).Error);
    }

    [Fact]
    public void A_result_closes_the_exchange_with_its_outcome()
    {
        var exchange = OpenExchange();

        Assert.True(exchange.Close(Billing, T0.AddMinutes(1), ExchangeOutcome.Blocked).Succeeded);

        Assert.Equal(ExchangeState.Closed, exchange.State);
        Assert.Equal(ExchangeOutcome.Blocked, exchange.Outcome);
        Assert.Equal(T0.AddMinutes(1), exchange.ClosedUtc);
    }

    [Fact]
    public void A_closed_exchange_accepts_nothing_further()
    {
        var exchange = OpenExchange();
        exchange.Close(Billing, T0, ExchangeOutcome.Complete);

        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.Clarify(Billing, T0).Error);
        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.Close(Billing, T0, ExchangeOutcome.Complete).Error);
        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.CloseWithConversation(T0).Error);
        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.Deliver(T0).Error);
    }

    [Fact]
    public void Only_the_broker_records_closed()
    {
        // An agent writing `closed` would claim a person ended its own unfinished work. The schema
        // refuses it too; refusing it here names the mistake.
        var exchange = OpenExchange();

        Assert.Equal(TransitionError.ClosedIsTheBrokers, exchange.Close(Billing, T0, ExchangeOutcome.Closed).Error);
        Assert.Equal(ExchangeState.Open, exchange.State);

        Assert.True(exchange.CloseWithConversation(T0).Succeeded);
        Assert.Equal(ExchangeOutcome.Closed, exchange.Outcome);
    }

    [Fact]
    public void Clarify_flips_the_turn()
    {
        var exchange = OpenExchange();

        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(5)).Succeeded);

        Assert.Equal(Orders, exchange.OwedBy);
        Assert.Equal(Billing, exchange.Waiting);
    }

    [Fact]
    public void The_receiver_cannot_send_the_result_while_the_requester_holds_the_turn()
    {
        var exchange = OpenExchange();
        exchange.Clarify(Billing, T0);

        Assert.Equal(TransitionError.NotYourTurn, exchange.Close(Billing, T0, ExchangeOutcome.Complete).Error);
    }

    [Fact]
    public void Clarify_out_of_turn_is_rejected()
    {
        var exchange = OpenExchange();

        // Billing owes the move; Orders has nothing to say yet.
        Assert.Equal(TransitionError.NotYourTurn, exchange.Clarify(Orders, T0).Error);
    }

    [Fact]
    public void Depth_counts_the_receivers_clarifications_but_not_the_replies()
    {
        var exchange = OpenExchange();

        exchange.Clarify(Billing, T0);
        Assert.Equal(1, exchange.ClarifyDepth);

        exchange.Clarify(Orders, T0);
        Assert.Equal(1, exchange.ClarifyDepth);

        exchange.Clarify(Billing, T0);
        Assert.Equal(2, exchange.ClarifyDepth);
    }

    [Fact]
    public void A_set_clarify_cap_refuses_and_leaves_the_exchange_open()
    {
        // Nothing closes the exchange on the receiver's behalf: an outcome is its judgement about its own
        // work, so it sends a Result with what it has.
        var exchange = OpenExchange(clarifyCap: 1);

        exchange.Clarify(Billing, T0);
        exchange.Clarify(Orders, T0);

        Assert.Equal(TransitionError.ClarifyDepthExceeded, exchange.Clarify(Billing, T0).Error);
        Assert.Equal(ExchangeState.Open, exchange.State);
        Assert.Null(exchange.Outcome);
    }

    [Fact]
    public void Only_the_first_collection_is_recorded()
    {
        // A Request delivered again after a restart was collected before; restamping it would hide how
        // long it has really been owed.
        var exchange = OpenExchange();

        exchange.Deliver(T0.AddMinutes(1));
        exchange.Deliver(T0.AddHours(3));

        Assert.Equal(T0.AddMinutes(1), exchange.DeliveredUtc);
    }

    [Fact]
    public void The_thresholds_are_stamped_from_the_policy()
    {
        var policy = ExchangePolicy.Default with
        {
            UncollectedAfter = TimeSpan.FromMinutes(5),
            StalledAfter = TimeSpan.FromHours(12)
        };

        var exchange = Exchange.Open(1, Orders, Billing, T0, policy);

        Assert.Equal(TimeSpan.FromMinutes(5), exchange.UncollectedAfter);
        Assert.Equal(TimeSpan.FromHours(12), exchange.StalledAfter);
    }

    [Fact]
    public void A_request_to_oneself_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Exchange.Open(1, Orders, Orders, T0));
    }
}
