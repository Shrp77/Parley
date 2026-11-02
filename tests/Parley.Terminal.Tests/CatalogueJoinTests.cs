using Parley.Client.Seating;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the agents region says, in words, where the registry and the catalogue disagree.
/// </summary>
/// <remarks>
/// <para>
/// The three states of <c>data-model.md</c>, each written out. Two of them exist because the two
/// sources are not the same set: the registry holds agents that registered themselves (SPEC.md
/// §12.3) and the catalogue holds what this machine could start, and neither is a subset of the
/// other. Before this feature the difference was visible nowhere, which is how SPEC.md §17
/// question 13's misleading <c>startable</c> stayed invisible.
/// </para>
/// <para>
/// The derivation is tested apart from the screen because it is where the judgement lives.
/// <c>RegionTests</c> proves the region draws; this proves it draws the right thing.
/// </para>
/// </remarks>
[Integration]
public sealed class CatalogueJoinTests : IDisposable
{
    private readonly List<Catalogued> _machines = [];

    public void Dispose()
    {
        foreach (var machine in _machines)
        {
            machine.Dispose();
        }
    }

    private static AgentRecord Agent(
        string name,
        string adapter = "mcp",
        AgentStatus status = AgentStatus.Active,
        AgentPresence presence = AgentPresence.Running) =>
        new(AgentName.Create(name), adapter, AgentTier.Pull, null, null, null, true, status, null, presence);

    /// <summary>A machine that can start one checkout per name given.</summary>
    /// <param name="roles">What each checkout answers to.</param>
    /// <returns>Its catalogue.</returns>
    /// <remarks>
    /// **The names come out of the repositories now** (018), so the checkouts have to exist and have to
    /// go on existing while this test runs: the catalogue says how to start an identity and nothing about
    /// what anything is called. The helper this replaced wrote one role-keyed file and deleted it, which
    /// against the current reader is a machine that can start nothing — and every assertion below about
    /// what the join says would have passed by there being nothing to say it about.
    /// </remarks>
    private NodeCatalogue Catalogue(params string[] roles)
    {
        var machine = Catalogued.Offering(roles);

        _machines.Add(machine);

        return machine.Catalogue;
    }

    [Fact]
    public void An_agent_in_both_reads_as_the_registry_says()
    {
        var rows = AgentsRegion.Join(
            [Agent("orders@test")], Catalogue("orders"), Catalogued.Project);

        Assert.Equal("running", Assert.Single(rows).State);
    }

    [Fact]
    public void A_checkout_renamed_since_it_registered_is_not_listed_a_second_time()
    {
        // The broker still holds the old name until the agent attaches under the new one, and the repository already
        // answers to the new one. The identity is what says they are one agent.
        var machine = Catalogued.Offering("payments");
        _machines.Add(machine);

        var registered = Agent("orders@" + NodeCatalogue.Machine) with { Identity = machine.Identities["payments"] };

        var rows = AgentsRegion.Join([registered], machine.Catalogue, Catalogued.Project);

        Assert.Equal("orders@" + NodeCatalogue.Machine, Assert.Single(rows).Name);
    }

    [Fact]
    public void A_registration_with_no_identity_is_matched_by_the_role_as_before()
    {
        var rows = AgentsRegion.Join(
            [Agent("orders@" + NodeCatalogue.Machine)], Catalogue("orders"), Catalogued.Project);

        Assert.Single(rows);
    }

    [Fact]
    public void An_agent_only_in_the_catalogue_says_it_has_not_registered()
    {
        // What adding an agent from this surface produces, and the state SC-003 promises within
        // ten seconds. It cannot say `startable`: §14 defines that for an agent the registry
        // holds, and nothing here may register on a repository's behalf.
        var rows = AgentsRegion.Join([], Catalogue("orders"), Catalogued.Project);

        Assert.Equal(AgentsRegion.CataloguedOnly, Assert.Single(rows).State);
    }

    [Fact]
    public void An_agent_only_in_the_registry_says_this_catalogue_does_not_name_it()
    {
        // §17 question 13's case, said out loud. The agent reads as startable to every peer and
        // this machine's node was never told it could start it, so nothing ever will.
        var rows = AgentsRegion.Join(
            [Agent("orders@test", presence: AgentPresence.Startable)],
            Catalogue("billing"),
            Catalogued.Project);

        var orders = Assert.Single(rows, row => row.Name == "orders@test");

        Assert.Contains("startable", orders.State, StringComparison.Ordinal);
        Assert.Contains(AgentsRegion.NotCatalogued, orders.State, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seat_reads_as_a_seat_and_is_never_marked_uncatalogued()
    {
        // Research R7. A person's seat is not a repository and no catalogue would ever name one,
        // so annotating it as missing from the catalogue would be permanent noise on the one row
        // that is always correct.
        var rows = AgentsRegion.Join(
            [Agent("arvind@test", adapter: Seat.Adapter)],
            Catalogue("orders"),
            Catalogued.Project);

        var seat = Assert.Single(rows, row => row.Name == "arvind@test");

        Assert.Equal(AgentsRegion.Seated, seat.State);
        Assert.True(seat.Seat);
        Assert.DoesNotContain(AgentsRegion.NotCatalogued, seat.State, StringComparison.Ordinal);
    }

    [Fact]
    public void Retirement_outranks_presence()
    {
        // Roster.State's judgement, inherited rather than re-made: a retired agent can still hold
        // a connection, and a row that read presence first would show it as running, which is the
        // one thing it certainly is not from the protocol's point of view.
        var rows = AgentsRegion.Join(
            [Agent("orders@test", status: AgentStatus.Retired)],
            Catalogue("orders"),
            Catalogued.Project);

        Assert.Equal("retired", Assert.Single(rows).State);
    }

    [Fact]
    public void A_project_with_no_catalogue_draws_the_registry_alone()
    {
        var rows = AgentsRegion.Join([Agent("orders@test")], NodeCatalogue.Empty);

        var orders = Assert.Single(rows);

        Assert.Contains(AgentsRegion.NotCatalogued, orders.State, StringComparison.Ordinal);
    }

    [Fact]
    public void A_catalogue_name_that_is_not_a_well_formed_agent_name_still_gets_a_row()
    {
        // The probe. Dropping it would hide a hand-edited file's mistake in the one place somebody
        // would look for it, and the node refuses to start it for the same reason — so a surface
        // that showed nothing would leave them with a role that exists in a file and nowhere else.
        //
        // **A repository may still declare one**, which is where names come from since 018; what cannot
        // happen any more is a catalogue *key* that is not an agent name, and that case is reported
        // separately as unreadable rather than drawn as a row.
        var rows = AgentsRegion.Join([], Catalogue("not a name"), Catalogued.Project);

        var row = Assert.Single(rows);

        Assert.Equal("not a name", row.Name);
        Assert.Equal(AgentsRegion.CataloguedOnly, row.State);
    }

    [Fact]
    public void Rows_are_ordered_by_name_whichever_source_they_came_from()
    {
        // So that a refresh cannot reorder the list under somebody's hand as agents register.
        var rows = AgentsRegion.Join(
            [Agent("orders@test"), Agent("alpha@test")],
            Catalogue("billing"),
            Catalogued.Project);

        // A registration is `role@machine` and a catalogue key is a role, so the two sources spell a
        // row differently — which is the inconsistency the owner met when the composer refused the
        // bare name this list had just printed. Recorded rather than papered over: the list still
        // shows each source as it holds it, and the composer accepts either spelling now.
        Assert.Equal(
            ["alpha@test", "billing", "orders@test"],
            rows.Select(row => row.Name));
    }
}
