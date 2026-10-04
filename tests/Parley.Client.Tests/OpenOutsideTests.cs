using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That an agent open outside Parley is said to be, on the room and on the agents list, and is never shown working or
/// stopped on what a node said of a different process (SPEC.md §11.7).
/// </summary>
public sealed class OpenOutsideTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@box");

    private static AgentRecord Outside() =>
        new(Billing, "claude-code", AgentTier.Pull, null, null, null, true, AgentStatus.Active,
            Presence: AgentPresence.Running,
            Turns: new TurnStanding(true, DateTimeOffset.UtcNow, null, false, 0),
            OpenOutside: true);

    [Fact]
    public void The_room_says_it_is_open_outside_Parley_whatever_its_node_last_reported()
    {
        var export = new ConversationExport([], []);

        var activity = Assert.Single(Activities.Of(export, [], [Outside()])).Value;

        Assert.Equal(Doing.Outside, activity.Doing);
        Assert.Equal(Activities.OutsideMark, Activities.Mark(activity));
        Assert.Equal(Activities.Outside, Activities.Says(activity, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_agents_list_says_Parley_cannot_give_it_turns()
    {
        var row = Assert.Single(Standing.Join([Outside()], new HashSet<string>(), "parley-console", "box"));

        Assert.Equal(Standing.Outside, row.Standing);
    }
}
