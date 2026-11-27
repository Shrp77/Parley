using Parley.Client.Views;
using Parley.Web.Pages.Shared;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>The agents list's badge is the state's own mark, never read back out of how the state is worded.</summary>
/// <remarks>
/// Feature 042. On a phone the badge is all that shows, so a running agent drawn with the not-running mark reads as
/// stopped; an older node that reports no turns must read as running, never stopped.
/// </remarks>
[Unit]
public sealed class AgentStateMarkTests
{
    [Fact]
    public void The_badge_is_the_states_mark_and_the_words_lose_the_mark_they_begin_with()
    {
        var (mark, words) = AgentStateMark.Split(new AgentNow($"{Activities.WorkingMark} working 1m", 0, Working: true, Mark: Activities.WorkingMark));

        Assert.Equal(Activities.WorkingMark, mark);
        Assert.Equal("working 1m", words);
    }

    [Theory]
    [InlineData("running", Activities.RunningMark)]
    [InlineData("left running by a node that is gone", Activities.OutsideMark)]
    public void Words_with_no_mark_keep_the_states_own_mark(string said, string kept)
    {
        var (mark, words) = AgentStateMark.Split(new AgentNow(said, 2, Mark: kept));

        Assert.Equal(kept, mark);
        Assert.Equal(said, words);
    }

    [Theory]
    [InlineData(true, Activities.OutsideMark, "left running by a node that is gone")]
    [InlineData(false, Activities.RunningMark, "running")]
    public void An_agent_left_running_or_on_an_older_node_is_badged_by_what_the_list_says(bool left, string kept, string said)
    {
        var agent = new Parley.Core.AgentRecord(
            Parley.Core.AgentName.Create("orders@test"), "mcp", Parley.Core.AgentTier.Full, null, null, null, true,
            Parley.Core.AgentStatus.Active, null, Parley.Core.AgentPresence.Running, OpenOutside: left, Unsupervised: left);

        var (mark, words) = AgentStateMark.Split(Activities.Now(agent, DateTimeOffset.UnixEpoch));

        Assert.Equal(kept, mark);
        Assert.NotEqual(Activities.NotRunningMark, mark);
        Assert.Equal(said, words);
    }

    [Fact]
    public void A_state_that_needs_no_attention_has_a_plain_badge()
    {
        Assert.Equal(string.Empty, AgentStateMark.Tint(Activities.NotRunningMark));
        Assert.Equal("state-stopped", AgentStateMark.Tint(Activities.StoppedMark));
    }
}
