using Xunit;

namespace Parley.Core.Tests;

public sealed class WaitForGraphTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly AgentName Shipping = AgentName.Create("shipping@test");
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

    private static Exchange Open(long id, AgentName asker, AgentName answerer) =>
        Exchange.Open(id, asker, answerer, T0, Policy);

    [Fact]
    public void Empty_graph_blocks_nothing()
    {
        var graph = new WaitForGraph();

        Assert.False(graph.WouldDeadlock(Orders, Billing, out _));
    }

    [Fact]
    public void Two_cycle_is_detected()
    {
        // Pair serialisation catches this too, but the graph must agree with it.
        var graph = WaitForGraph.FromOpenExchanges([Open(1, Billing, Orders)]);

        Assert.True(graph.WouldDeadlock(Orders, Billing, out var path));
        Assert.Equal([Orders, Billing], path);
    }

    [Fact]
    public void Three_cycle_is_detected()
    {
        // The case pair locking cannot see: every pair lock is free, every pair holds
        // exactly one question, and all three agents end up blocked.
        var graph = WaitForGraph.FromOpenExchanges(
        [
            Open(1, Billing, Inventory),
            Open(2, Inventory, Orders)
        ]);

        Assert.True(graph.WouldDeadlock(Orders, Billing, out var path));
        Assert.Equal([Orders, Billing, Inventory], path);
    }

    [Fact]
    public void Four_cycle_is_detected()
    {
        var graph = WaitForGraph.FromOpenExchanges(
        [
            Open(1, Billing, Inventory),
            Open(2, Inventory, Shipping),
            Open(3, Shipping, Orders)
        ]);

        Assert.True(graph.WouldDeadlock(Orders, Billing, out var path));
        Assert.Equal([Orders, Billing, Inventory, Shipping], path);
    }

    [Fact]
    public void Chain_without_a_cycle_is_allowed()
    {
        var graph = WaitForGraph.FromOpenExchanges(
        [
            Open(1, Billing, Inventory),
            Open(2, Inventory, Shipping)
        ]);

        Assert.False(graph.WouldDeadlock(Orders, Billing, out _));
    }

    [Fact]
    public void Clarify_reverses_the_edge()
    {
        // While the answerer holds the turn, the asker waits. A clarification hands the turn
        // back, so it is now the answerer that waits on the asker.
        var exchange = Open(1, Orders, Billing);
        exchange.Clarify(Billing, T0);

        var graph = WaitForGraph.FromOpenExchanges([exchange]);

        // Billing now waits on Orders. Orders asking Billing would therefore close the loop;
        // Billing asking Orders would not, because Billing is already the waiting party.
        Assert.True(graph.WouldDeadlock(Orders, Billing, out _));
        Assert.False(graph.WouldDeadlock(Billing, Orders, out _));
    }

    [Fact]
    public void Closed_exchanges_contribute_no_edges()
    {
        var exchange = Open(1, Billing, Orders);
        exchange.Answer(Orders, T0);

        var graph = WaitForGraph.FromOpenExchanges([exchange]);

        Assert.False(graph.WouldDeadlock(Orders, Billing, out _));
    }

    [Fact]
    public void Orphaned_exchanges_contribute_no_edges()
    {
        // An orphan holds no pair slot, so it must not block a new question either.
        var exchange = Open(1, Billing, Orders);
        exchange.Orphan(T0.AddMinutes(20));

        var graph = WaitForGraph.FromOpenExchanges([exchange]);

        Assert.False(graph.WouldDeadlock(Orders, Billing, out _));
    }

    [Fact]
    public void Diamond_without_a_cycle_terminates()
    {
        // Two paths reach shipping; the visited set must stop the second from re-walking.
        var graph = new WaitForGraph();
        graph.AddWait(Billing, Inventory);
        graph.AddWait(Billing, Shipping);
        graph.AddWait(Inventory, Shipping);

        Assert.False(graph.WouldDeadlock(Orders, Billing, out _));
    }
}
