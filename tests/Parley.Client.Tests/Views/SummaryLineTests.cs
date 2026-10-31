using Parley.Client.Views;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// The one line both surfaces put beside a project, at every value it can take.
/// </summary>
/// <remarks>
/// <para>
/// This line is read while somebody is scanning rather than
/// reading, which is the condition under which a wrong number is least likely to be questioned —
/// and it is composed in one place precisely so that two surfaces cannot say different things
/// about the same project.
/// </para>
/// <para>
/// The boundary that matters is not zero but <i>unknown</i>. A broker that did not answer has an
/// unknown number of agents, and rendering that as none says something the console does not know.
/// </para>
/// </remarks>
[Unit]
public sealed class SummaryLineTests
{
    [Fact]
    public void An_unreachable_project_says_why_and_never_says_a_number()
    {
        var line = ProjectSummary.Unreachable("This project's broker is not answering.").Line();

        Assert.Equal("This project's broker is not answering.", line);
        Assert.DoesNotContain("0 agent", line, StringComparison.Ordinal);
        Assert.DoesNotContain("conversation", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_project_says_none_of_each_rather_than_nothing()
    {
        // A project with a broker that answered and holds nothing is a real state and a different
        // one. Saying nothing at all would leave it looking like the row above.
        var line = new ProjectSummary(0, 0, 0, 0, null).Line();

        Assert.Contains("0 agents", line, StringComparison.Ordinal);
        Assert.Contains("0 conversations", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 agent")]
    [InlineData(2, "2 agents")]
    public void One_of_something_is_not_pluralised(int agents, string expected)
    {
        // Small, and the kind of thing that survives for years once shipped because nobody wants to
        // be the person who filed it.
        Assert.Contains(expected, new ProjectSummary(agents, 0, 0, 0, null).Line(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_running_is_left_out_rather_than_said_as_zero()
    {
        // "3 agents, 0 running" is noise on every row of a machine nobody has open. What a person
        // is scanning for is the rows where something *is* running.
        var quiet = new ProjectSummary(3, 0, 0, 0, null).Line();
        var busy = new ProjectSummary(3, 2, 0, 0, null).Line();

        Assert.DoesNotContain("running", quiet, StringComparison.Ordinal);
        Assert.Contains("2 running", busy, StringComparison.Ordinal);
    }

    [Fact]
    public void Archived_conversations_are_named_only_when_there_are_some()
    {
        var none = new ProjectSummary(1, 0, 4, 0, null).Line();
        var some = new ProjectSummary(1, 0, 4, 2, null).Line();

        Assert.DoesNotContain("archived", none, StringComparison.Ordinal);
        Assert.Contains("2 archived", some, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_built_from_a_read_that_failed_is_unreachable_whichever_half_failed()
    {
        // Two reads make one summary. Either failing means the line cannot be trusted, and a
        // summary that reported the half that worked would be a number with no warning on it.
        var registryFailed = ProjectsView.Summarise(null, 4, 0, "the registry could not be read");
        var conversationsFailed = ProjectsView.Summarise([], null, 0, "the list could not be read");

        Assert.False(registryFailed.Reachable);
        Assert.False(conversationsFailed.Reachable);
    }

    [Fact]
    public void A_summary_of_a_project_that_answered_counts_what_is_running()
    {
        var summary = ProjectsView.Summarise([], 0, 0, null);

        Assert.True(summary.Reachable);
        Assert.Equal(0, summary.Agents);
        Assert.Equal(0, summary.Running);
    }
}
