using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>Each background task the runner lists is read by its id, kind and label (feature 043, SPEC.md §11.6).</summary>
/// <remarks>
/// <para>
/// The lines are what Claude Code 2.1.332 wrote, run as a node runs it (research R1): every running task is listed whole
/// each time the set changes, each with the runner's <c>task_id</c>, its <c>task_type</c> and the description the agent
/// gave the tool call that started it.
/// </para>
/// <para>
/// <b>The count is never lost to the list.</b> A task the node cannot name (an id it would not hold) is still counted, and
/// a list past the bound is counted whole and named to the bound.
/// </para>
/// </remarks>
[Unit]
public sealed class RunnerBackgroundTasksTests
{
    private const string TwoTasks =
        """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"af7da04eab7667f03","run_id":"0mwxtqj6h-ec48e1d9","task_type":"local_agent","subagent_type":"general-purpose","description":"Count to three"},{"task_id":"b8n705a3e","run_id":"0mwxtqjnd-c1916130","task_type":"local_bash","description":"Wait twenty-five seconds"}],"uuid":"u","session_id":"s"}""";

    [Fact]
    public void Each_task_is_read_with_its_id_kind_and_label_in_the_runners_order()
    {
        var read = ResidentAgent.RunnerEvent.Read(TwoTasks)!;

        Assert.Equal(2, read.Tasks);
        Assert.Equal(
            [
                new BackgroundTask("af7da04eab7667f03", TaskKind.Agent, "Count to three"),
                new BackgroundTask("b8n705a3e", TaskKind.Command, "Wait twenty-five seconds"),
            ],
            read.Listed);
    }

    [Fact]
    public void An_empty_list_is_no_tasks()
    {
        var read = ResidentAgent.RunnerEvent.Read("""{"type":"system","subtype":"background_tasks_changed","tasks":[]}""")!;

        Assert.Equal(0, read.Tasks);
        Assert.Empty(read.Listed!);
    }

    [Fact]
    public void A_task_with_no_description_or_another_kind_is_listed_unnamed()
    {
        var read = ResidentAgent.RunnerEvent.Read(
            """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"x1","task_type":"remote_thing"}]}""")!;

        Assert.Equal([new BackgroundTask("x1", TaskKind.Other, string.Empty)], read.Listed);
    }

    [Theory]
    [InlineData("""{"task_type":"local_bash","description":"No id"}""")]
    [InlineData("""{"task_id":"has space","description":"Bad id"}""")]
    [InlineData("""{"task_id":42,"description":"Number id"}""")]
    [InlineData("\"just a string\"")]
    public void A_task_the_node_cannot_name_is_counted_and_not_listed(string entry)
    {
        var read = ResidentAgent.RunnerEvent.Read(
            $$"""{"type":"system","subtype":"background_tasks_changed","tasks":[{{entry}},{"task_id":"ok","description":"Kept"}]}""")!;

        Assert.Equal(2, read.Tasks);
        Assert.Equal([new BackgroundTask("ok", TaskKind.Other, "Kept")], read.Listed);
    }

    [Fact]
    public void A_label_is_held_to_a_names_bound_and_neutralised()
    {
        var label = "Line one\\nline two " + new string('x', 10_000);
        var read = ResidentAgent.RunnerEvent.Read(
            $$"""{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"t","description":"{{label}}"}]}""")!;

        var name = Assert.Single(read.Listed!).Name;

        Assert.DoesNotContain('\n', name);
        Assert.True(new System.Globalization.StringInfo(name).LengthInTextElements <= BackgroundTaskText.LongestName);
        Assert.StartsWith("Line one line two", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_past_the_bound_is_counted_whole_and_listed_to_the_bound()
    {
        var entries = string.Join(',', Enumerable.Range(0, 250).Select(i => $$"""{"task_id":"t{{i}}","description":"Task {{i}}"}"""));
        var read = ResidentAgent.RunnerEvent.Read(
            $$"""{"type":"system","subtype":"background_tasks_changed","tasks":[{{entries}}]}""")!;

        Assert.Equal(250, read.Tasks);
        Assert.Equal(BackgroundTaskText.MostTasks, read.Listed!.Count);
        Assert.Equal("t0", read.Listed[0].Id);
    }

    [Fact]
    public void A_line_with_no_list_lists_nothing()
    {
        var read = ResidentAgent.RunnerEvent.Read("""{"type":"system","subtype":"init","session_id":"s"}""")!;

        Assert.Null(read.Tasks);
        Assert.Null(read.Listed);
    }
}
