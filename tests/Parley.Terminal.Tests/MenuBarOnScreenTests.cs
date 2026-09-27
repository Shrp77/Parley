using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the menu bar is on the screen, on the top row, and that the panes moved down for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These read the laid-out screen rather than a property, and that is the whole reason the file
/// exists separately from `EveryActionIsOnTheBarTests`.</b> CLAUDE.md records four occasions when this
/// surface held a property the suite checked and the screen never showed: <c>Shell.Said</c> written in
/// twenty places and read by nothing, a terminal with no colour under a passing <c>ColourTests</c>, the
/// line pane with no size at all, and a box with no rows while typing still worked. A menu bar that
/// existed only in <c>BarText()</c> would be the fifth.
/// </para>
/// <para>
/// <b>The row it costs is the thing to check, not just the text.</b> D3 chose an always-visible bar over
/// a popover knowing it costs a row permanently, and this surface budgets rows tightly — so a bar drawn
/// over the top of a pane, or one that took its row twice, is the failure worth catching.
/// </para>
/// </remarks>
public sealed class MenuBarOnScreenTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public void The_bar_is_drawn_on_the_top_row()
    {
        // Must fail against a bar that exists as text and is never placed -- which is what happened to
        // the line pane, whose region existed, was visible, held text, and drew nothing.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);
        driving.App.LayoutAndDraw(true);

        var screen = driving.Text().ReplaceLineEndings("\n").Split('\n');

        Assert.NotEmpty(screen);
        Assert.Contains("Alt+", screen[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_bar_names_a_category_the_focused_region_actually_has()
    {
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        var said = shell.BarText();

        // The projects view's own actions are the project's, and health and the web UI are the
        // deployment's however they are reached.
        Assert.Contains("Project", said, StringComparison.Ordinal);
        Assert.Contains("Deployment", said, StringComparison.Ordinal);

        // And Help, which belongs to no region at all.
        Assert.Contains("Help", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bar_takes_one_row_and_the_panes_start_below_it()
    {
        // **The row is counted once.** Must fail against panes still starting at row zero, which would
        // draw the bar over the top of one, and against the height being reduced twice, which would
        // leave a blank row above the status line that nothing accounts for.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);
        driving.App.LayoutAndDraw(true);

        var pane = shell.Projects.Frame;

        Assert.Equal(Shell.BarRows, pane.Y);

        // The bar, the message line and the status line are the only chrome, so what is left for the
        // panes is the height less those three.
        Assert.Equal(30 - Shell.BarRows - 2, pane.Height);
    }

    [Fact]
    public async Task The_bar_changes_with_the_focused_region()
    {
        // **D2, observable.** The bar is scoped to focus, so moving focus changes what is on it -- and a
        // bar that did not change would be offering one region's actions while another had the cursor,
        // which is the cross-region read this surface has recorded five times.
        //
        // **Driven into the project view rather than the projects list**, which a first draft got wrong:
        // that view has exactly one pane, so `Focus(1)` moved nothing and the bar was rightly identical.
        // The test was asserting a change the surface had no way to make.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Parley.Client.Views.Viewing.Project);
        shell.Lay(120);

        Assert.True(shell.Panes.Count > 1, "this view is meant to have more than one pane");

        shell.Focus(0);
        shell.Lay(120);

        var onAgents = shell.BarText();

        shell.Focus(1);
        shell.Lay(120);

        Assert.NotEqual(onAgents, shell.BarText());

        // And the two name the categories their own regions have: agents against conversations.
        Assert.Contains("Agent", onAgents, StringComparison.Ordinal);
        Assert.Contains("Conversation", shell.BarText(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_line_still_names_the_way_back_and_the_actions_key()
    {
        // The narrow rule, which the bar must not have taken a row from. 021 says so in as many words,
        // and 0.18.4 records what a status line missing the way out costs: the only route off a screen
        // was a key nothing named.
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(40);

        Assert.True(shell.Status.Length <= 40, $"the status line is {shell.Status.Length} columns");
        Assert.Contains($"{Shell.ActionsKey}:", shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrow_terminal_still_gets_a_bar()
    {
        // It is the one row that must not be dropped by the ladder: the bar is what makes the actions
        // discoverable without knowing about `?`, which is the complaint 021 answers. Cutting it at
        // narrow widths would put the feature back where it started on the terminals that need it most.
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(40);
        driving.App.LayoutAndDraw(true);

        Assert.NotEmpty(shell.BarText());

        var screen = driving.Text().ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("Alt+", screen[0], StringComparison.Ordinal);
    }
}
