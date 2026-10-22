using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The two moves an agent makes when the person's words settled something (SPEC.md §5.6): closing its own question to
/// the person, and taking back the turn on the person's request it had asked back about.
/// </summary>
/// <remarks>
/// Only what is true of one exchange is checked here. That the other party is the person, and that the settling
/// exchange is the person's Request in the same conversation, are checked by the broker and by storage.
/// </remarks>
public sealed class SettlingTransitionsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // orders asked the person something: the person owes the move.
    private static Exchange Asked() => Exchange.Open(7, Orders, Person, T0);

    // The person asked orders something, and orders asked back: the person owes the move.
    private static Exchange AskedBack()
    {
        var exchange = Exchange.Open(8, Person, Orders, T0);

        Assert.True(exchange.Clarify(Orders, T0).Succeeded);

        return exchange;
    }

    [Fact]
    public void The_requester_settles_its_own_question_and_it_closes_settled_naming_what_settled_it()
    {
        var exchange = Asked();
        var at = T0.AddMinutes(5);

        var moved = exchange.Settle(Orders, 42, at);

        Assert.True(moved.Succeeded);
        Assert.Equal(ExchangeState.Closed, exchange.State);
        Assert.Equal(ExchangeOutcome.Settled, exchange.Outcome);
        Assert.Equal(42, exchange.SettledBy);
        Assert.Equal(at, exchange.ClosedUtc);
    }

    [Fact]
    public void The_receiver_cannot_settle_a_question_put_to_it()
    {
        var exchange = Asked();

        Assert.Equal(TransitionError.NotAParticipant, exchange.Settle(Person, 42, T0).Error);
        Assert.Equal(ExchangeState.Open, exchange.State);
    }

    [Fact]
    public void Nobody_outside_the_exchange_can_settle_it()
    {
        Assert.Equal(TransitionError.NotAParticipant, Asked().Settle(Inventory, 42, T0).Error);
    }

    [Fact]
    public void A_question_the_requester_owes_the_move_on_cannot_be_settled()
    {
        // The person asked back, so orders owes the next move: nothing the person said later settled anything.
        var exchange = Asked();

        Assert.True(exchange.Clarify(Person, T0).Succeeded);

        Assert.Equal(TransitionError.NotYourTurn, exchange.Settle(Orders, 42, T0).Error);
        Assert.Equal(ExchangeState.Open, exchange.State);
    }

    [Fact]
    public void A_closed_exchange_cannot_be_settled()
    {
        var exchange = Asked();

        Assert.True(exchange.Close(Person, T0, ExchangeOutcome.Complete).Succeeded);

        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.Settle(Orders, 42, T0).Error);
        Assert.Equal(ExchangeOutcome.Complete, exchange.Outcome);
        Assert.Null(exchange.SettledBy);
    }

    [Fact]
    public void An_exchange_cannot_settle_itself()
    {
        Assert.Throws<ArgumentException>(() => Asked().Settle(Orders, 7, T0));
    }

    [Fact]
    public void Turning_back_hands_the_receiver_the_turn_and_moves_nothing_else()
    {
        var exchange = AskedBack();
        var depth = exchange.ClarifyDepth;

        var moved = exchange.TurnBack(Orders);

        Assert.True(moved.Succeeded);
        Assert.Equal(Orders, exchange.OwedBy);
        Assert.Equal(ExchangeState.Open, exchange.State);
        Assert.Equal(depth, exchange.ClarifyDepth);
        Assert.Null(exchange.Outcome);
    }

    [Fact]
    public void Only_the_receiver_turns_back_and_only_when_the_requester_owes_the_move()
    {
        Assert.Equal(TransitionError.NotAParticipant, AskedBack().TurnBack(Person).Error);
        Assert.Equal(TransitionError.NotAParticipant, AskedBack().TurnBack(Inventory).Error);

        // Nobody asked back yet: the receiver already holds the turn, so there is nothing to take.
        Assert.Equal(TransitionError.NotYourTurn, Exchange.Open(9, Person, Orders, T0).TurnBack(Orders).Error);
    }

    [Fact]
    public void Turning_back_a_closed_exchange_is_refused()
    {
        var exchange = AskedBack();

        Assert.True(exchange.CloseWithConversation(T0).Succeeded);

        Assert.Equal(TransitionError.ExchangeNotOpen, exchange.TurnBack(Orders).Error);
    }

    [Fact]
    public void Rehydrating_keeps_what_settled_it()
    {
        var exchange = Exchange.Rehydrate(
            7, Orders, Person, Person, ExchangeState.Closed, 0, ExchangePolicy.UnboundedClarifications,
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(4), T0, null, T0, ExchangeOutcome.Settled, 1, settledBy: 42);

        Assert.Equal(42, exchange.SettledBy);
    }
}
