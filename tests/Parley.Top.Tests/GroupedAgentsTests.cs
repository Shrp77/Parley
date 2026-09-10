using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the agents region groups by state, and that the state is readable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner running it.</b> A row was <c>name [standing] owns workspace</c>, and in
/// the half-width pane a project view gives this region, a 27-character agent name and a
/// 50-character standing are over the width before the ownership line begins. The one fact
/// somebody opens this screen to read was the one cut off.
/// </para>
/// <para>
/// The state is a heading now and a row is a name. That is a smaller claim than it sounds and it
/// is worth checking rather than assuming: the state has to appear exactly once per group, the
/// names have to be findable, and a heading must never be a thing the cursor can act on.
/// </para>
/// </remarks>
public sealed class GroupedAgentsTests
{
    private static AgentRecord Agent(
        string name, AgentPresence presence = AgentPresence.Running,
        AgentStatus status = AgentStatus.Active) =>
        new(AgentName.Create(name), "mcp", AgentTier.Pull, "/srv", null, null, true, status,
            null, presence);

    private static AgentRecord Seat(string name) =>
        new(AgentName.Create(name), Client.Seating.Seat.Adapter, AgentTier.Pull, null, null, null,
            true, AgentStatus.Active, null, AgentPresence.Running);

    [Fact]
    public void A_row_carries_the_name_and_the_heading_carries_the_state()
    {
        var region = new AgentsRegion();

        region.Show([Agent("orders@test")], NodeCatalogue.Empty, null);

        var rows = Drawn.Rows(region.List);

        // The name, indented, and nothing else on it. The standing, the ownership line and the
        // workspace were all on this row and are what pushed the state off the screen.
        Assert.Contains(rows, row => row.Trim() == "orders@test");
        Assert.DoesNotContain(rows, row => row.Contains('[', StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("/srv", StringComparison.Ordinal));

        // And the state is said once, above it, at column zero.
        var heading = rows.Single(row => !row.StartsWith(' '));

        Assert.Equal(Standing.Running, heading);
    }

    [Fact]
    public void Seats_are_their_own_group_and_come_first()
    {
        var region = new AgentsRegion();

        region.Show(
            [Agent("zulu@test"), Seat("arvind@test"), Agent("alpha@test")],
            NodeCatalogue.Empty,
            null);

        var rows = Drawn.Rows(region.List);

        Assert.Equal(AgentsRegion.Seats, rows[0]);
        Assert.Equal("arvind@test", rows[1].Trim());

        // A person is not an agent, so their row is not filed under a state software can be in.
        Assert.NotEqual(Standing.Running, AgentsRegion.Seats);
    }

    [Fact]
    public void Names_are_alphabetical_within_a_group()
    {
        var region = new AgentsRegion();

        region.Show(
            [Agent("zulu@test"), Agent("alpha@test"), Agent("mike@test")],
            NodeCatalogue.Empty,
            null);

        var names = Drawn.Rows(region.List)
            .Where(row => row.StartsWith(' '))
            .Select(row => row.Trim())
            .ToList();

        // Ordinary alphabetical order. The comparison in the source ignores case, which for an
        // agent cannot matter -- AgentName's own pattern is lowercase -- and costs nothing to keep
        // for a seat name arriving from somewhere with looser rules.
        Assert.Equal(["alpha@test", "mike@test", "zulu@test"], names);
    }

    [Fact]
    public void Each_state_is_named_once_however_many_agents_are_in_it()
    {
        var region = new AgentsRegion();

        region.Show(
            [Agent("a@test"), Agent("b@test"), Agent("c@test")],
            NodeCatalogue.Empty,
            null);

        var rows = Drawn.Rows(region.List);

        Assert.Single(rows, row => !row.StartsWith(' '));
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void A_heading_is_not_something_the_cursor_can_hold()
    {
        // The property every action on this region depends on. A cursor resting on a heading is a
        // position where `Selected` is null and every action has quietly gone away, which reads as
        // the surface having broken rather than as the cursor being somewhere that means nothing.
        var region = new AgentsRegion();

        region.Show([Agent("orders@test"), Seat("arvind@test")], NodeCatalogue.Empty, null);

        var rows = Drawn.Rows(region.List);

        for (var at = 0; at < rows.Count; at++)
        {
            region.List.SelectedItem = at;

            Assert.NotNull(region.Selected);
        }
    }

    [Fact]
    public void Grouping_puts_a_retired_agent_apart_from_a_running_one()
    {
        // The two a person most needs to tell apart, and the pair the owner could not read because
        // the standing was off the end of the row.
        var groups = AgentsRegion.Grouped(
            AgentsRegion.Join(
                [Agent("live@test"), Agent("gone@test", status: AgentStatus.Retired)],
                NodeCatalogue.Empty),
            Standing.Join(
                [Agent("live@test"), Agent("gone@test", status: AgentStatus.Retired)],
                new HashSet<string>(StringComparer.Ordinal),
                Client.Seating.Seat.Adapter,
                MachineName.Current));

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.Heading == Standing.Retired);
        Assert.All(groups, group => Assert.Single(group.Rows));
    }

    [Fact]
    public void A_withdrawn_seat_is_not_filed_with_the_ones_that_can_be_taken()
    {
        // **Found by drawing the owner's own deployment rather than by a test.** Both seats on it
        // were retired, and one seats group made a seat the broker will refuse look exactly like one
        // it will not. Enter explains it, but a list that has to be acted on to be understood has
        // not said what it knows.
        var region = new AgentsRegion();

        region.Show(
            [
                Seat("live@test"),
                new AgentRecord(
                    AgentName.Create("gone@test"), Client.Seating.Seat.Adapter, AgentTier.Pull,
                    null, null, null, true, AgentStatus.Retired, null, AgentPresence.Running),
            ],
            NodeCatalogue.Empty,
            null);

        var rows = Drawn.Rows(region.List);

        Assert.Equal(AgentsRegion.Seats, rows[0]);
        Assert.Equal("live@test", rows[1].Trim());
        Assert.Equal(AgentsRegion.RetiredSeats, rows[2]);
        Assert.Equal("gone@test", rows[3].Trim());
    }
}
