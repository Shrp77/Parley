using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the frame's timed reload of a list page keeps where the reader had scrolled to and which rows were open.
/// </summary>
/// <remarks>
/// Owner, 2026-11-20: on All Agents the list reloaded every ten seconds and threw the reader to the top. The reload is a new
/// navigation, and on a grid page the scroller is the grid and not the window, whose rows also arrive after the page does. There
/// is no script engine in these tests, so each guard is read from the layout's source (<see cref="ScriptSource"/>).
/// </remarks>
[Integration]
public sealed class TheTimersReloadKeepsTheReadersPlaceTests
{
    private static string Reload => ScriptSource.Code(ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml"), "parley-reload");

    [Fact]
    public void The_place_is_written_before_the_reload_and_the_grid_scroller_is_in_it()
    {
        Assert.Contains("sessionStorage.setItem(key, JSON.stringify({", Reload, StringComparison.Ordinal);
        Assert.Contains("grid: grid ? grid.scrollTop : 0", Reload, StringComparison.Ordinal);
        Assert.Contains("y: window.scrollY", Reload, StringComparison.Ordinal);
        Assert.Contains("details.dgrid-row[open]", Reload, StringComparison.Ordinal);

        // Written before the page is replaced, and not after.
        Assert.True(
            Reload.IndexOf("keep(); location.replace(", StringComparison.Ordinal)
            > Reload.IndexOf("sessionStorage.setItem(key", StringComparison.Ordinal));
    }

    [Fact]
    public void The_place_is_taken_back_as_rows_arrive_and_only_for_the_same_page_until_the_reader_moves()
    {
        Assert.Contains("kept.path === location.pathname", Reload, StringComparison.Ordinal);
        Assert.Contains("grid.scrollTop = kept.grid;", Reload, StringComparison.Ordinal);
        Assert.Contains("setTimeout(again, 150)", Reload, StringComparison.Ordinal);
        Assert.Contains("\"wheel\", \"keydown\", \"pointerdown\", \"touchstart\"", Reload, StringComparison.Ordinal);

        // Read once and removed, so a later visit by link starts at the top.
        Assert.Contains("sessionStorage.removeItem(key);", Reload, StringComparison.Ordinal);
    }
}
