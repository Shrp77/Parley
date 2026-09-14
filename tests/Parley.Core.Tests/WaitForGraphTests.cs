using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The cycle a Request would close, which the broker records and admits (§5.0).
/// </summary>
/// <remarks>
/// A cycle is not refused in 0.5: agents asking each other for work in a loop is ordinary cross-repository
/// work, and nothing is deadlocked because nothing waits synchronously. It is recorded so the person running
/// the deployment can see a loop that has stopped making progress.
/// </remarks>
public sealed class WaitForGraphTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly AgentName Shipping = AgentName.Create("shipping@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Exchange Open(long id, AgentName requester, AgentName receiver) =>
        Exchange.Open(id, requester, receiver, T0);

    [Fact]
    public void An_empty_graph_closes_nothing()
    {
        var graph = new WaitForGraph();

        Assert.False(graph.ClosesCycle(Orders, Billing, out _));
    }

    [Fact]
    public void A_two_cycle_is_detected()
    {
        var graph = WaitForGraph.FromOpenExchanges([Open(1, Billing, Orders)]);

        Assert.True(graph.ClosesCycle(Orders, Billing, out var path));
        Assert.Equal([Orders, Billing], path);
    }

    [Fact]
    public void A_three_cycle_is_detected()
    {
        // The case no pair can see: every pair holds exactly one Request, and the loop spans all three.
        var graph = WaitForGraph.FromOpenExchanges(
        [
            Open(1, Billing, Inventory),
            Open(2, Inventory, Orders)
        ]);

        Assert.True(graph.ClosesCycle(Orders, Billing, out var path));
        Assert.Equal([Orders, Billing, Inventory], path);
    }

    [Fact]
    public void A_four_cycle_is_detected()
    {
        var graph = WaitForGraph.FromOpenExchanges(
        [
            Open(1, Billing, Inventory),
            Open(2, Inventory, Shipping),
            Open(3, Shipping, Orders)
        ]);

        Assert.True(graph.ClosesCycle(Orders, Billing, out var path));
        Assert.Equal([Orders, Billing, Inventory, Shipping], path);
    }

    [Fact]
    public void A_chain_without_a_cycle_closes_nothing()
    {
        var graph = WaitForGraph.FromOpenExchanges(
        [
            Open(1, Billing, Inventory),
            Open(2, Inventory, Shipping)
        ]);

        Assert.False(graph.ClosesCycle(Orders, Billing, out _));
    }

    [Fact]
    public void Clarify_reverses_the_edge()
    {
        // While the receiver holds the turn, the requester waits. A clarification hands the turn back, so
        // the receiver now waits on the requester.
        var exchange = Open(1, Orders, Billing);
        exchange.Clarify(Billing, T0);

        var graph = WaitForGraph.FromOpenExchanges([exchange]);

        Assert.True(graph.ClosesCycle(Orders, Billing, out _));
        Assert.False(graph.ClosesCycle(Billing, Orders, out _));
    }

    [Fact]
    public void Closed_exchanges_contribute_no_edges()
    {
        var exchange = Open(1, Billing, Orders);
        exchange.Close(Orders, T0, ExchangeOutcome.Complete);

        var graph = WaitForGraph.FromOpenExchanges([exchange]);

        Assert.False(graph.ClosesCycle(Orders, Billing, out _));
    }

    [Fact]
    public void A_diamond_without_a_cycle_terminates()
    {
        // Two paths reach shipping; the visited set must stop the second from re-walking.
        var graph = new WaitForGraph();
        graph.AddWait(Billing, Inventory);
        graph.AddWait(Billing, Shipping);
        graph.AddWait(Inventory, Shipping);

        Assert.False(graph.ClosesCycle(Orders, Billing, out _));
    }
}
