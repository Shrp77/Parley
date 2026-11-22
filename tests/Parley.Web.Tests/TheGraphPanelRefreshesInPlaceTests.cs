using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the graph panel's timed refresh leaves what the person did to the panel as it was.
/// </summary>
/// <remarks>
/// The panel swapped its whole body every fifteen seconds whatever had changed, so the list a person opened to read
/// snapped shut within one refresh, the link they were on lost the focus, and a screen reader was handed the whole graph
/// again (review, lens 1 run 1 on 0.64.4). The list is the picture's equal, so it has to be readable for longer than that.
/// Read from the script as code, comments out, so a guard that is commented out does not count as there.
/// It reads the layout from the repository, a file, so it is an integration test like the other script guards.
/// </remarks>
[Integration]
public sealed class TheGraphPanelRefreshesInPlaceTests
{
    private static string Layout => ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");

    private static string Script => ScriptSource.Code(Layout, "\"parley.graph\"");

    [Fact]
    public void An_answer_that_says_what_is_already_drawn_is_not_swapped_in()
    {
        // Its stamp aside, which differs every time.
        Assert.Contains("var same = html.replace(/data-asof=\"[^\"]*\"/, \"\");", Script, StringComparison.Ordinal);
        Assert.Contains("if (same === drawn) { return; }", Script, StringComparison.Ordinal);
        Assert.Contains("drawn = same;", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_swap_keeps_the_list_open_the_focus_on_its_link_and_the_scroll_where_it_was()
    {
        Assert.Contains("var open = !!(list && list.open);", Script, StringComparison.Ordinal);
        Assert.Contains("if (again) { again.open = true; }", Script, StringComparison.Ordinal);
        Assert.Contains("if (links[i].getAttribute(\"href\") === href) { links[i].focus(); break; }", Script, StringComparison.Ordinal);
        Assert.Contains("body.scrollTop = top;", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void An_older_answer_that_lands_late_is_dropped_and_a_failed_read_keeps_the_graph_already_drawn()
    {
        Assert.Contains("var mine = ++asked;", Script, StringComparison.Ordinal);
        Assert.Contains("if (mine !== asked) { return; }", Script, StringComparison.Ordinal);
        Assert.Contains("if (drawn === null) { body.textContent =", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panels_body_is_not_a_live_region()
    {
        Assert.Contains("<div class=\"pg-body\" id=\"pg-body\"></div>", Layout, StringComparison.Ordinal);
    }
}
