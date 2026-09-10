using Parley.Client.Seating;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

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
public sealed class CatalogueJoinTests
{
    private static AgentRecord Agent(
        string name,
        string adapter = "mcp",
        AgentStatus status = AgentStatus.Active,
        AgentPresence presence = AgentPresence.Running) =>
        new(AgentName.Create(name), adapter, AgentTier.Pull, null, null, null, true, status, null, presence);

    private static NodeCatalogue Catalogue(params string[] roles)
    {
        var path = Path.Combine(Path.GetTempPath(), "parley-join-" + Path.GetRandomFileName() + ".json");

        var entries = roles.Select(role =>
            $"    \"{role}\": {{ \"command\": \"claude\", \"workspace\": \"/srv/{role}\" }}");

        File.WriteAllText(path, "{\n  \"agents\": {\n" + string.Join(",\n", entries) + "\n  }\n}\n");

        try
        {
            return NodeCatalogue.ReadFrom(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_agent_in_both_reads_as_the_registry_says()
    {
        var rows = AgentsRegion.Join([Agent("orders@test")], Catalogue("orders"));

        Assert.Equal("running", Assert.Single(rows).State);
    }

    [Fact]
    public void An_agent_only_in_the_catalogue_says_it_has_not_registered()
    {
        // What adding an agent from this surface produces, and the state SC-003 promises within
        // ten seconds. It cannot say `startable`: §14 defines that for an agent the registry
        // holds, and nothing here may register on a repository's behalf.
        var rows = AgentsRegion.Join([], Catalogue("orders"));

        Assert.Equal(AgentsRegion.CataloguedOnly, Assert.Single(rows).State);
    }

    [Fact]
    public void An_agent_only_in_the_registry_says_this_catalogue_does_not_name_it()
    {
        // §17 question 13's case, said out loud. The agent reads as startable to every peer and
        // this machine's node was never told it could start it, so nothing ever will.
        var rows = AgentsRegion.Join(
            [Agent("orders@test", presence: AgentPresence.Startable)],
            Catalogue("billing"));

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
            Catalogue("orders"));

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
            Catalogue("orders"));

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
        var rows = AgentsRegion.Join([], Catalogue("not a name"));

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
            Catalogue("billing"));

        // A registration is `role@machine` and a catalogue key is a role, so the two sources spell a
        // row differently — which is the inconsistency the owner met when the composer refused the
        // bare name this list had just printed. Recorded rather than papered over: the list still
        // shows each source as it holds it, and the composer accepts either spelling now.
        Assert.Equal(
            ["alpha@test", "billing", "orders@test"],
            rows.Select(row => row.Name));
    }
}
