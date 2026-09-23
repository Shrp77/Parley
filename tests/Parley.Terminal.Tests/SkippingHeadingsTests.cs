using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the cursor keys cross a group heading, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner: Up did nothing.</b> Grouping the agents by state put a heading above
/// each group, and the cursor is not allowed to rest on one — so a move that lands on a heading
/// carries on past it. That settled downwards whichever way the cursor had been going, so Up
/// stepped onto the heading above and was pushed straight back onto the row it left. Every group
/// was a room with no door out of the top of it, and the seats — which sort first — could not be
/// reached from anywhere below them at all.
/// </para>
/// <para>
/// <b>Nothing in the suite pressed an arrow key.</b> The guard was written and checked by
/// assigning <c>SelectedItem</c> directly, which is the one route that really does only ever go
/// one way. <c>Lists</c> already records the same shape once — <i>a key tested only in the
/// negative is a key nobody has pressed</i> — and this is the stronger version of it: a key
/// nobody had pressed at all, in a suite of two and a half thousand tests, guarding the only
/// navigation this surface has.
/// </para>
/// </remarks>
public sealed class SkippingHeadingsTests
{
    private static AgentRecord Agent(
        string name, AgentPresence presence = AgentPresence.Running,
        AgentStatus status = AgentStatus.Active) =>
        new(AgentName.Create(name), "mcp", AgentTier.Pull, "/srv", null, null, true, status,
            null, presence);

    private static AgentRecord Seat(string name, AgentStatus status = AgentStatus.Active) =>
        new(AgentName.Create(name), Client.Seating.Seat.Adapter, AgentTier.Pull, null, null, null,
            true, status, null, AgentPresence.Running);

    /// <summary>The owner's own shape: a retired seat above, and agents in two states below it.</summary>
    /// <returns>The region, filled.</returns>
    /// <remarks>
    /// Three groups rather than two, because two would let a test pass that could cross one
    /// heading and no more — and the seat the owner was reaching for was two headings up.
    /// </remarks>
    private static AgentsRegion Deployment()
    {
        var region = new AgentsRegion();

        region.Show(
            [
                Seat("sam@test", AgentStatus.Retired),
                Agent("orders@test", AgentPresence.Startable),
                Agent("billing@test"),
            ],
            NodeCatalogue.Empty,
            null);

        return region;
    }

    [Fact]
    public void Up_reaches_the_group_above()
    {
        var region = Deployment();

        region.List.SelectedItem = Row(region, "orders@test");

        region.List.NewKeyDownEvent(Key.CursorUp);

        Assert.Equal("billing@test", region.Selected?.Name);
    }

    [Fact]
    public void Up_reaches_the_seats_from_the_bottom_of_the_list()
    {
        // The owner's report exactly: down in the startable group, trying to get to a seat two
        // headings above, with nothing but Up to do it with.
        var region = Deployment();
        var rows = Drawn.Rows(region.List);

        region.List.SelectedItem = rows.Count - 1;

        for (var press = 0; press < rows.Count; press++)
        {
            region.List.NewKeyDownEvent(Key.CursorUp);
        }

        Assert.Equal("sam@test", region.Selected?.Name);
    }

    [Fact]
    public void Down_reaches_the_group_below()
    {
        var region = Deployment();

        region.List.SelectedItem = Row(region, "sam@test");

        region.List.NewKeyDownEvent(Key.CursorDown);

        Assert.Equal("billing@test", region.Selected?.Name);
    }

    [Fact]
    public void Home_lands_on_a_name_rather_than_the_heading_above_it()
    {
        // Home travels up and the first row of a grouped list is always a heading, so the
        // direction it settles has to turn back at the end rather than run off it.
        var region = Deployment();
        var rows = Drawn.Rows(region.List);

        region.List.SelectedItem = rows.Count - 1;

        region.List.NewKeyDownEvent(Key.Home);

        Assert.NotNull(region.Selected);
        Assert.Equal("sam@test", region.Selected?.Name);
    }

    [Fact]
    public void End_lands_on_a_name()
    {
        var region = Deployment();

        region.List.SelectedItem = 1;

        region.List.NewKeyDownEvent(Key.End);

        Assert.NotNull(region.Selected);
    }

    [Fact]
    public void Every_row_the_cursor_can_reach_by_key_is_something_to_act_on()
    {
        // The property the actions depend on, checked by walking the list the way a person does
        // rather than by assigning to every index in turn — which is how it came to be believed.
        var region = Deployment();
        var rows = Drawn.Rows(region.List);

        region.List.SelectedItem = 0;

        for (var press = 0; press < rows.Count * 2; press++)
        {
            Assert.NotNull(region.Selected);

            region.List.NewKeyDownEvent(press < rows.Count ? Key.CursorDown : Key.CursorUp);
        }

        Assert.NotNull(region.Selected);
    }

    /// <summary>Which row carries a name.</summary>
    /// <param name="region">The region.</param>
    /// <param name="name">The name.</param>
    /// <returns>Its row number.</returns>
    private static int Row(AgentsRegion region, string name)
    {
        var rows = Drawn.Rows(region.List);
        var at = rows.FindIndex(row => row.Trim() == name);

        Assert.True(at >= 0, $"no row reads '{name}'");

        return at;
    }
}
