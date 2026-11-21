using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>Who a conversation shows as working on an answer.</summary>
[Unit]
public sealed class WorkingOnTests
{
    private static readonly AgentName Person = AgentName.Create("orders@here");
    private static readonly AgentName Billing = AgentName.Create("billing@here");
    private static readonly AgentName Stock = AgentName.Create("inventory@here");
    private static readonly DateTimeOffset Noon = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly ConversationExport OneOpen = new(
        [],
        [new ExchangeSnapshot(1, ExchangeState.Open, Noon, null), new ExchangeSnapshot(2, ExchangeState.Open, Noon, null)],
        null);

    [Fact]
    public void Working_and_owing_here_is_shown_from_when_the_turn_started()
    {
        var found = Activities.WorkingOn(
            OneOpen,
            [new WaitingEdge(Person, Billing, 1)],
            new Dictionary<AgentName, Activity> { [Billing] = new(Doing.Working, Noon) });

        Assert.Equal(new WorkingHere(Billing, Noon), Assert.Single(found));
    }

    [Theory]
    [InlineData(Doing.Stopped)]
    [InlineData(Doing.Running)]
    [InlineData(Doing.NotRunning)]
    [InlineData(Doing.Outside)]
    public void Anything_else_it_may_be_doing_is_not_working(Doing doing)
    {
        // A stopped agent carries a time too, and it is not when anything started.
        var found = Activities.WorkingOn(
            OneOpen,
            [new WaitingEdge(Person, Billing, 1)],
            new Dictionary<AgentName, Activity> { [Billing] = new(doing, Noon) });

        Assert.Empty(found);
    }

    [Fact]
    public void Two_exchanges_owed_are_one_entry_and_the_longest_running_comes_first()
    {
        var found = Activities.WorkingOn(
            OneOpen,
            [new WaitingEdge(Person, Billing, 1), new WaitingEdge(Stock, Billing, 2), new WaitingEdge(Person, Stock, 2)],
            new Dictionary<AgentName, Activity>
            {
                [Billing] = new(Doing.Working, Noon),
                [Stock] = new(Doing.Working, Noon.AddMinutes(-5)),
            });

        Assert.Equal([Stock, Billing], found.Select(entry => entry.Agent));
    }

    [Fact]
    public void An_agent_is_not_working_on_a_conversation_it_has_already_answered_or_one_the_export_does_not_hold()
    {
        // Review, quality lens (0.62.9): the rule that owing an open exchange here is required survived a mutation that
        // dropped it, because every case held only open exchanges and only edges that named them.
        var closed = new ConversationExport(
            [],
            [new ExchangeSnapshot(1, ExchangeState.Closed, Noon, Noon.AddMinutes(1))],
            null);

        var working = new Dictionary<AgentName, Activity> { [Billing] = new(Doing.Working, Noon) };

        Assert.Empty(Activities.WorkingOn(closed, [new WaitingEdge(Person, Billing, 1)], working));
        Assert.Empty(Activities.WorkingOn(OneOpen, [new WaitingEdge(Person, Billing, 99)], working));
    }

    [Fact]
    public void An_agent_the_registry_does_not_name_has_no_entry()
    {
        Assert.Empty(Activities.WorkingOn(OneOpen, [new WaitingEdge(Person, Billing, 1)], new Dictionary<AgentName, Activity>()));
    }
}
