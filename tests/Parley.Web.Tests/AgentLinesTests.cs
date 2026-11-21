using Parley.Client.Rendering;
using Parley.Core;
using Parley.Web.Pages;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The agents grid is agent-centric: one line for each agent, with every project that lists it on that line.
/// </summary>
/// <remarks>
/// An agent is <c>role@machine</c>, one repository on one machine, and a repository may be enrolled in several projects.
/// The grid used to be drawn a block per project, so that agent was listed once for each of them.
/// </remarks>
[Unit]
public sealed class AgentLinesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 7, 0, 0, TimeSpan.Zero);

    private static AgentRow Row(string project, string name, string? owns = null, AgentStatus status = AgentStatus.Active, bool running = false)
    {
        var agent = new AgentRecord(
            AgentName.Create(name), "claude-code", AgentTier.Full, null, null, null, running, status,
            Owns: owns, Presence: running ? AgentPresence.Running : AgentPresence.Startable);

        return new AgentRow(project, agent, Roster.State(agent), Parley.Client.Views.Activities.Now(agent, Now));
    }

    [Fact]
    public void An_agent_several_projects_list_is_one_line_with_each_project_on_it()
    {
        var lines = AgentLine.Group(
        [
            Row("alpha", "orders@pc"), Row("beta", "orders@pc"), Row("alpha", "billing@pc"),
        ]);

        Assert.Equal(2, lines.Count);

        var orders = Assert.Single(lines, line => line.Name.Role == "orders");

        Assert.Equal(["alpha", "beta"], orders.Rows.Select(row => row.Project));
        Assert.Equal(["alpha"], Assert.Single(lines, line => line.Name.Role == "billing").Rows.Select(row => row.Project));
    }

    [Fact]
    public void The_same_role_on_two_machines_is_two_agents()
    {
        var lines = AgentLine.Group([Row("alpha", "orders@pc"), Row("alpha", "orders@laptop")]);

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void A_line_says_what_the_most_active_of_its_projects_says_and_takes_the_first_stated_ownership()
    {
        var running = Row("beta", "orders@pc", owns: "Owns the orders.", running: true);
        var asleep = Row("alpha", "orders@pc");

        var line = Assert.Single(AgentLine.Group([asleep, running]));

        Assert.Equal("beta", line.Best.Project);
        Assert.Equal("Owns the orders.", line.Owns);
    }

    [Fact]
    public void A_line_with_no_ownership_stated_says_nothing_rather_than_something_made_up()
    {
        Assert.Equal(string.Empty, Assert.Single(AgentLine.Group([Row("alpha", "orders@pc")])).Owns);
    }

    [Fact]
    public void No_rows_make_no_lines_and_a_null_list_is_refused()
    {
        Assert.Empty(AgentLine.Group([]));
        Assert.Throws<ArgumentNullException>(() => AgentLine.Group(null!));
    }

    [Fact]
    public void The_lines_are_ordered_most_active_first_then_by_machine_and_role()
    {
        var lines = AgentLine.Group(
        [
            Row("alpha", "zed@pc"), Row("alpha", "ann@pc"), Row("alpha", "busy@pc", running: true),
        ]);

        Assert.Equal(["busy", "ann", "zed"], lines.Select(line => line.Name.Role));
    }
}
