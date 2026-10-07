using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the room lists every agent of the project and marks the ones that have taken part, as drawn (specs/026).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads the drawn rows rather than the data</b>, because 0.37.7 recorded the opposite failing: a property the
/// suite held to — Parley kept off the agents list — and the screen never showed, since the join sat one line before
/// the filter and every test read what the filter produced.
/// </para>
/// </remarks>
public sealed class RoomRosterOnScreenTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@here");
    private static readonly AgentName Billing = AgentName.Create("billing@here");
    private static readonly AgentName Inventory = AgentName.Create("inventory@here");
    private static readonly AgentName Ledger = AgentName.Create("ledger@here");
    private static readonly AgentName Person = AgentName.Create("human-user@alpha");

    private static readonly IReadOnlyDictionary<AgentName, Activity> Doing = new Dictionary<AgentName, Activity>
    {
        [Orders] = new(Parley.Client.Views.Doing.Running, null),
        [Billing] = new(Parley.Client.Views.Doing.Running, null),
        [Inventory] = new(Parley.Client.Views.Doing.NotRunning, null),
        [Ledger] = new(Parley.Client.Views.Doing.Running, null),
    };

    private static RoomRegion Shown(bool seated, int? width = null)
    {
        var room = width is { } w ? new RoomRegion { Frame = new System.Drawing.Rectangle(0, 0, w, 40) } : new RoomRegion();

        room.Show(
            [new PairState(Orders, null, Turn.Nobody), new PairState(Billing, null, Turn.Nobody)],
            [],
            seated,
            seated ? Person : null,
            activity: Doing,
            others: [Inventory, Ledger]);

        return room;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_agent_of_the_project_is_drawn_whether_or_not_a_seat_is_held(bool seated)
    {
        var rows = Shown(seated).Written;

        foreach (var agent in new[] { "orders", "billing", "inventory", "ledger" })
        {
            Assert.Contains(rows, row => row.Contains(agent, StringComparison.Ordinal));
        }

        Assert.Contains(rows, row => row.Contains(RoomRegion.Participants, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(RoomRegion.Elsewhere, StringComparison.Ordinal));
    }

    [Fact]
    public void The_agents_that_took_part_carry_the_mark_and_the_others_do_not()
    {
        var rows = Shown(seated: true).Written;

        Assert.Contains(rows, row => row.Contains(RoomRoster.ParticipantMark, StringComparison.Ordinal) && row.Contains("orders", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(RoomRoster.ParticipantMark, StringComparison.Ordinal) && row.Contains("billing", StringComparison.Ordinal));

        Assert.DoesNotContain(rows, row => row.Contains(RoomRoster.ParticipantMark, StringComparison.Ordinal) && row.Contains("inventory", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains(RoomRoster.ParticipantMark, StringComparison.Ordinal) && row.Contains("ledger", StringComparison.Ordinal));

        // Said on screen, because a mark nothing explains is a fact that exists only for whoever wrote it.
        Assert.Contains(RoomRoster.ParticipantLegend, rows);
    }

    [Fact]
    public void An_agent_not_in_the_conversation_shows_what_it_is_doing_like_the_rest()
    {
        var rows = Shown(seated: true).Written;

        Assert.Contains(rows, row => row.Contains($"{Activities.NotRunningMark} inventory", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains($"{Activities.RunningMark} ledger", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_in_the_project_is_listed_twice_and_Parley_and_the_person_are_not_among_the_agents()
    {
        var rows = Shown(seated: true).Written;

        foreach (var agent in new[] { "orders", "billing", "inventory", "ledger" })
        {
            Assert.Single(rows, row => row.Contains(agent, StringComparison.Ordinal) && row.Contains("]", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(rows, row => row.Contains("parley-facilitator", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(28)]
    [InlineData(40)]
    [InlineData(80)]
    public void No_row_is_cut_off_at_a_narrow_width_and_the_mark_and_name_survive(int width)
    {
        var rows = Shown(seated: true, width).Written;

        Assert.All(rows, row => Assert.True(row.Length <= width, $"{row.Length} > {width}: '{row}'"));
        Assert.Contains(rows, row => row.Contains(RoomRoster.ParticipantMark, StringComparison.Ordinal) && row.Contains("billing", StringComparison.Ordinal));
    }
}
