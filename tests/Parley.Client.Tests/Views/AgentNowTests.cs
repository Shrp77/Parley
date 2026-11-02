using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>That a list of agents says who is working, and claims nothing it does not know (SPEC.md §11.6).</summary>
[Unit]
public sealed class AgentNowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static AgentRecord Agent(
        TurnStanding? turns = null,
        AgentPresence presence = AgentPresence.Running,
        AgentStatus status = AgentStatus.Active,
        bool outside = false) =>
        new(AgentName.Create("orders@test"), "mcp", AgentTier.Full, null, null, null,
            presence == AgentPresence.Running, status, null, presence, Turns: turns, OpenOutside: outside);

    private static TurnStanding Turns(DateTimeOffset? running = null, DateTimeOffset? ended = null, int denials = 0) =>
        new(true, running, ended, denials == 0, denials);

    [Fact]
    public void A_running_turn_reads_as_working_for_how_long_and_sorts_first()
    {
        var now = Activities.Now(Agent(Turns(running: Now.AddMinutes(-4))), Now);

        Assert.Equal("◆ working 4m", now.Words);
        Assert.True(now.Working);
        Assert.Equal(0, now.Rank);
    }

    [Fact]
    public void Attached_with_no_turn_reads_as_idle_not_as_running()
    {
        var now = Activities.Now(Agent(Turns(ended: Now.AddHours(-1))), Now);

        Assert.Equal("● idle", now.Words);
        Assert.False(now.Working);
    }

    [Fact]
    public void A_refused_permission_is_said_and_sorts_above_the_idle()
    {
        var stopped = Activities.Now(Agent(Turns(ended: Now.AddHours(-2), denials: 1)), Now);
        var idle = Activities.Now(Agent(Turns()), Now);

        Assert.Equal("■ stopped 2h00m — 1 permission refused", stopped.Words);
        Assert.True(stopped.Rank < idle.Rank);
    }

    [Fact]
    public void An_agent_whose_turns_nobody_reports_claims_neither_working_nor_idle()
    {
        Assert.Equal("running", Activities.Now(Agent(), Now).Words);
        Assert.Equal("running", Activities.Now(Agent(TurnStanding.Unreported), Now).Words);
    }

    [Fact]
    public void Open_outside_Parley_and_retired_outrank_whatever_a_turn_says()
    {
        var turn = Turns(running: Now.AddMinutes(-1));

        Assert.Equal("open outside Parley", Activities.Now(Agent(turn, outside: true), Now).Words);
        Assert.Equal("retired", Activities.Now(Agent(turn, status: AgentStatus.Retired), Now).Words);
    }

    [Fact]
    public void Not_attached_reads_by_its_presence()
    {
        Assert.Equal("startable", Activities.Now(Agent(Turns(), AgentPresence.Startable), Now).Words);
        Assert.Equal("stranded", Activities.Now(Agent(Turns(), AgentPresence.Stranded), Now).Words);
    }

    [Theory]
    [InlineData(true, Standing.Working)]
    [InlineData(false, Standing.Ready)]
    public void The_terminals_headings_follow_the_same_turns(bool inATurn, string heading)
    {
        var rows = Standing.Join(
            [Agent(Turns(running: inATurn ? Now : null))], new HashSet<string>(), "parley-console", "test");

        Assert.Equal(heading, Assert.Single(rows).Standing);
    }

    [Fact]
    public void A_refused_permission_gets_its_own_heading_and_an_unreported_agent_keeps_running()
    {
        var refused = Standing.Join(
            [Agent(Turns(ended: Now, denials: 2))], new HashSet<string>(), "parley-console", "test");
        var unreported = Standing.Join([Agent()], new HashSet<string>(), "parley-console", "test");

        Assert.Equal(Standing.StoppedRefused, Assert.Single(refused).Standing);
        Assert.Equal(Standing.Running, Assert.Single(unreported).Standing);
    }
}
