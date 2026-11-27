using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>An agent whose turn ended while one background task it started runs on is waiting on that work (SPEC.md §11.6).</summary>
/// <remarks>
/// Found by review, lens 3 run I on 19cd7a08: every case held two tasks, so the Inbox chip could start at two
/// (<c>BackgroundTasks: &gt; 1</c>) and call an agent with one task "Idle", and "1 task" could read "1 tasks", with every
/// test still passing. One task is the common case: a single sub-agent.
/// </remarks>
[Unit]
public sealed class OneBackgroundTaskIsBackgroundWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void One_background_task_is_background_work_on_the_Inbox_chip_and_is_counted_as_one()
    {
        var turns = new TurnStanding(true, null, Now.AddMinutes(-1), true, 0, BackgroundTasks: 1);
        var agent = new AgentRecord(
            AgentName.Create("orders@alpha"), "claude-code", AgentTier.Headless, null, null, null, true, AgentStatus.Active,
            Presence: AgentPresence.Running, Turns: turns);

        var standing = InFlightStanding.From(agent, [], 1);

        Assert.Equal(AgentState.Background, standing.State);
        Assert.Equal("Background work", standing.Chip);
        Assert.Contains("(1 task)", standing.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("1 tasks", standing.Detail, StringComparison.Ordinal);
    }
}
