using Parley.Client;
using Parley.Client.Seating;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a refresh moves nobody's highlight onto something else.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate. Every destructive action on this surface acts on whatever is
/// selected, and a redraw happens on every project change, every report and every write — so a
/// selection that followed a <i>position</i> rather than a <i>name</i> would eventually retire the
/// agent below the one somebody was looking at.
/// </para>
/// <para>
/// Probed against the two things that change under a selection: a list that grows, and the
/// unreachable-broker line that appears above the rows and shifts every one of them down.
/// </para>
/// </remarks>
public sealed class SelectionTests
{
    /// <summary>Puts the cursor on the row that names one agent.</summary>
    /// <param name="region">The region.</param>
    /// <param name="name">The agent.</param>
    /// <remarks>
    /// <b>By name rather than by index, which is the change grouping forced.</b> These tests said
    /// <c>SelectedItem = 0</c> and meant "the first agent"; row zero is a group heading now, and a
    /// heading is not selectable — so the old spelling put the cursor nowhere and the region
    /// correctly answered null. Naming the agent says what the test always meant.
    /// </remarks>
    private static void Select(AgentsRegion region, string name)
    {
        var rows = Drawn.Rows(region.List);
        var at = rows.FindIndex(row => row.Trim() == name);

        Assert.True(at >= 0, $"no row names {name}: " + string.Join(" | ", rows));

        region.List.SelectedItem = at;

        Assert.Equal(name, region.Selected?.Name);
    }

    private static AgentRecord Agent(string name) =>
        new(AgentName.Create(name), "mcp", AgentTier.Pull, "/srv", null, null, true, AgentStatus.Active,
            null, AgentPresence.Running);

    [Fact]
    public void An_agent_selection_follows_the_agent_when_the_list_grows_above_it()
    {
        var region = new AgentsRegion();

        region.Show([Agent("orders@test")], NodeCatalogue.Empty, null);
        Select(region, "orders@test");

        Assert.Equal("orders@test", region.Selected?.Name);

        // A new agent registers, and sorts above the one selected.
        region.Show([Agent("orders@test"), Agent("alpha@test")], NodeCatalogue.Empty, null);

        Assert.Equal("orders@test", region.Selected?.Name);
    }

    [Fact]
    public void An_agent_selection_survives_a_broker_that_stops_answering_and_comes_back()
    {
        // The offset case, and the one that would be silent. When the broker cannot be asked, the
        // reason takes the first line and every row moves down one; a selection kept by position
        // would then name the row above the one highlighted, which in this region is the
        // difference between retiring the right agent and the wrong one.
        var region = new AgentsRegion();
        var catalogue = NodeCatalogue.Empty;

        region.Show([Agent("alpha@test"), Agent("orders@test")], catalogue, null);
        Select(region, "orders@test");

        Assert.Equal("orders@test", region.Selected?.Name);

        region.Show([Agent("alpha@test"), Agent("orders@test")], catalogue, "not answering");

        Assert.Equal("orders@test", region.Selected?.Name);

        region.Show([Agent("alpha@test"), Agent("orders@test")], catalogue, null);

        Assert.Equal("orders@test", region.Selected?.Name);
    }

    [Fact]
    public void Nothing_is_selected_when_the_first_line_is_the_reason_the_broker_did_not_answer()
    {
        // The row that is not a row. Somebody arrowing onto the reason must not be holding an
        // agent, or the next destructive action would take one they never pointed at.
        var region = new AgentsRegion();

        // **The promise is stronger than it was, and grouping is what made it so.** Landing on the
        // reason used to answer null; the cursor cannot land on it at all now, because unselectable
        // lines became an ordinary thing and the list settles off them. Asserted both ways: putting
        // the cursor there does not leave it there, and where it does leave it is a real agent.
        region.Show([Agent("orders@test")], NodeCatalogue.Empty, "not answering");

        region.List.SelectedItem = 0;

        Assert.NotEqual(0, region.List.SelectedItem);
        Assert.Equal("orders@test", region.Selected?.Name);
    }

    [Fact]
    public void A_conversation_selection_follows_the_conversation_rather_than_the_row()
    {
        var region = new ConversationsRegion();

        region.Show([Conversation("second"), Conversation("first")], null);
        region.List.SelectedItem = 1;

        Assert.Equal("first", region.Selected?.Id);

        // A new conversation arrives at the top, as the most recent.
        region.Show([Conversation("third"), Conversation("second"), Conversation("first")], null);

        Assert.Equal("first", region.Selected?.Id);
    }

    [Fact]
    public void A_project_selection_follows_the_project_when_another_is_named()
    {
        var region = new ProjectsRegion();
        var health = new Dictionary<string, ProjectHealth>(StringComparer.Ordinal);

        region.Show([Project("alpha"), Project("beta")], health, null);
        region.List.SelectedItem = 1;

        Assert.Equal("beta", region.Selected?.Name.Value);

        region.Show([Project("alpha"), Project("acme"), Project("beta")], health, null);

        Assert.Equal("beta", region.Selected?.Name.Value);
    }

    [Fact]
    public void A_selection_whose_row_has_gone_falls_back_rather_than_pointing_at_a_neighbour()
    {
        // What happens after a purge: the agent selected is no longer there. Silently holding the
        // index would leave the highlight on whoever took its place, one keystroke from being
        // withdrawn by somebody who thought they were still on the first.
        var region = new AgentsRegion();

        region.Show([Agent("alpha@test"), Agent("orders@test")], NodeCatalogue.Empty, null);
        Select(region, "orders@test");

        region.Show([Agent("alpha@test")], NodeCatalogue.Empty, null);

        Assert.Equal("alpha@test", region.Selected?.Name);
        Assert.Single(region.Rows);
    }

    [Fact]
    public void A_seat_row_never_becomes_the_selection_by_accident_of_sorting()
    {
        // Rows are ordered by name whatever they are, so a seat can sit between two agents. What
        // must not happen is a seat being what a withdrawal acts on, and the row carries that fact
        // rather than the caller re-deriving it.
        var seat = new AgentRecord(
            AgentName.Create("mid@test"), Seat.Adapter, AgentTier.Pull, null, null, null, true,
            AgentStatus.Active, null, AgentPresence.Running);

        var region = new AgentsRegion();

        region.Show([Agent("alpha@test"), seat, Agent("zulu@test")], NodeCatalogue.Empty, null);
        Select(region, "mid@test");

        Assert.True(region.Selected?.Seat);
    }

    private static ConversationSummary Conversation(string id) =>
        new(
            id,
            [AgentName.Create("orders@test")],
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            false);

    private static ProjectSettings Project(string name) =>
        new(ProjectName.Create(name), string.Empty, "http://127.0.0.1:1", null);
}
