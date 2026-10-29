using Parley.Client.Roadmaps;
using Parley.Client.Views;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Counting what is proposed beside what else is in a project, and never showing unknown as none (SPEC.md §5.7).
/// </summary>
public sealed class WaitingProposalsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData(0, "")]
    [InlineData(1, "1 roadmap proposed, for you to decide")]
    [InlineData(3, "3 roadmaps proposed, for you to decide")]
    public void What_is_said_depends_on_a_count_that_may_not_be_known(int? waiting, string says)
    {
        Assert.Equal(says, WaitingProposals.Says(waiting));
    }

    [Fact]
    public void The_projects_line_carries_the_count_only_when_there_is_one()
    {
        Assert.EndsWith("· 2 roadmaps proposed, for you to decide", new ProjectSummary(3, 1, 4, 0, null, 2).Line(), StringComparison.Ordinal);
        Assert.DoesNotContain("proposed", new ProjectSummary(3, 1, 4, 0, null, 0).Line(), StringComparison.Ordinal);
        Assert.DoesNotContain("proposed", new ProjectSummary(3, 1, 4, 0, null, null).Line(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_that_did_not_answer_says_why_and_nothing_about_proposals()
    {
        var line = ProjectSummary.Unreachable("no broker is listening").Line();

        Assert.Equal("no broker is listening", line);
    }
}
