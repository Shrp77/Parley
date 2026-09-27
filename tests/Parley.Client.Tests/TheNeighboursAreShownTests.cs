using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// What the other agents already claim, shown beside a proposal and never judged.
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-017 asks for the neighbours to be visible; FR-030 and Principle II forbid anything deciding
/// about them.</b> Those pull in opposite directions in exactly one place — an overlap — and this is the
/// file that holds the line. Two agents both claiming stock is a real state of a deployment, and it is
/// the person's to settle or to leave.
/// </para>
/// <para>
/// <b>Must fail against any arbitration creeping in</b>, which is the likely drift rather than an exotic
/// one: refusing an overlapping line, ranking the claims, marking one as the winner, or quietly dropping
/// the duplicate would each feel like helpfulness and would each be one agent given authority over
/// another in the one place nobody would look for it.
/// </para>
/// </remarks>
public sealed class TheNeighboursAreShownTests
{
    private static AgentRecord Agent(
        string name, string? owns, AgentStatus status = AgentStatus.Active) =>
        new(
            AgentName.Create(name),
            "mcp",
            AgentTier.Pull,
            "/src/thing",
            null,
            null,
            false,
            status,
            owns);

    [Fact]
    public void Two_agents_claiming_the_same_thing_are_both_shown_and_neither_is_refused()
    {
        // **AC US4.3.** The overlap is the case this exists for, so it is the first test. Must fail
        // against de-duplicating, against dropping either, and against any field or marker saying which
        // one is right -- the shape returned carries a name and a line and has nowhere to put a verdict.
        AgentRecord[] registry =
        [
            Agent("orders@test", "Orders, invoicing and stock."),
            Agent("inventory@test", "Stock and shipping."),
            Agent("billing@test", "Invoicing."),
        ];

        var neighbours = Roster.Neighbours(registry, AgentName.Create("billing@test"));

        Assert.Equal(2, neighbours.Count);
        Assert.Contains(neighbours, peer => peer.Name == "orders@test");
        Assert.Contains(neighbours, peer => peer.Name == "inventory@test");

        // Both claims reach the screen word for word. Nothing summarises, truncates or reconciles them.
        Assert.Contains(neighbours, peer => peer.Owns == "Orders, invoicing and stock.");
        Assert.Contains(neighbours, peer => peer.Owns == "Stock and shipping.");
    }

    [Fact]
    public void The_agent_being_decided_about_is_not_among_its_own_neighbours()
    {
        // It would read as a second opinion about itself, and on the accept screen its own line is
        // already shown above as what peers read today.
        AgentRecord[] registry =
        [
            Agent("orders@test", "Orders and invoicing."),
            Agent("billing@test", "Invoicing."),
        ];

        var neighbours = Roster.Neighbours(registry, AgentName.Create("orders@test"));

        Assert.DoesNotContain(neighbours, peer => peer.Name == "orders@test");
    }

    [Fact]
    public void An_agent_that_has_stated_nothing_is_left_out_rather_than_listed_blank()
    {
        // The point of reading the neighbours is to see what is already claimed. A row saying nothing is
        // noise, and on this screen noise costs the rows the proposal itself needs.
        AgentRecord[] registry =
        [
            Agent("orders@test", "Orders and invoicing."),
            Agent("quiet@test", null),
            Agent("blank@test", "   "),
        ];

        var neighbours = Roster.Neighbours(registry, AgentName.Create("billing@test"));

        var only = Assert.Single(neighbours);

        Assert.Equal("orders@test", only.Name);
    }

    [Fact]
    public void A_retired_agent_is_not_shown_as_a_neighbour()
    {
        // §10.2's retire stops an agent being routed to at all, so what it claims no longer competes
        // with the line being decided -- and showing it would invite somebody to narrow a live agent's
        // scope around an agent nothing will ever send to.
        AgentRecord[] registry =
        [
            Agent("orders@test", "Orders and invoicing."),
            Agent("old@test", "Everything, once.", AgentStatus.Retired),
        ];

        var neighbours = Roster.Neighbours(registry, AgentName.Create("billing@test"));

        var only = Assert.Single(neighbours);

        Assert.Equal("orders@test", only.Name);
    }

    [Fact]
    public void What_a_neighbour_claims_is_what_its_peers_read_and_not_a_pending_line()
    {
        // A line somebody accepted but which the agent has not come back with is a decision that has
        // reached nobody, so it is not what competes. Must fail against reading `OwnsAccepted` here,
        // which would show a claim no peer is acting on as though it were in force.
        AgentRecord[] registry =
        [
            Agent("orders@test", "Orders only.") with { OwnsAccepted = "Orders, invoicing and stock." },
        ];

        var only = Assert.Single(Roster.Neighbours(registry, AgentName.Create("billing@test")));

        Assert.Equal("Orders only.", only.Owns);
    }

    [Fact]
    public void An_empty_project_has_no_neighbours_rather_than_throwing()
    {
        // The first agent in a deployment is asked what it owns with nobody to compare against, which is
        // the ordinary first afternoon rather than an edge case.
        Assert.Empty(Roster.Neighbours([], AgentName.Create("orders@test")));
    }
}
