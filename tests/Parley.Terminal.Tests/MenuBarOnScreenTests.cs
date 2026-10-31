using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.Input;
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
[Integration]
public sealed class MenuBarOnScreenTests
{
    internal static BrokerDirectory Directory() =>
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
        Assert.Contains("Project", screen[0], StringComparison.Ordinal);
        Assert.Contains("Help", screen[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_bar_names_a_category_the_focused_region_actually_has()
    {
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        var said = shell.BarTitles();

        // The projects view's own actions are the project's, and health and the web UI are the
        // deployment's however they are reached.
        Assert.Contains("Project", said);
        Assert.Contains("Deployment", said);

        // And Help, which belongs to no region at all.
        Assert.Contains("Help", said);
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

        var onAgents = shell.BarTitles();

        shell.Focus(1);
        shell.Lay(120);

        Assert.NotEqual(onAgents, shell.BarTitles());

        // And the two name the categories their own regions have: agents against conversations.
        Assert.Contains("Agent", onAgents);
        Assert.Contains("Conversation", shell.BarTitles());

        // **And the drawn row follows, which is the half a list cannot show.** The bar is rebuilt at the
        // next layout rather than when it is told, so a bar that was told and never redrawn would pass
        // everything above.
        driving.App.LayoutAndDraw(true);

        Assert.Contains("Conversation", driving.Text().ReplaceLineEndings("\n").Split('\n')[0], StringComparison.Ordinal);
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

        // And no longer names `?` as the way to the rest: the bar is that, on the owner's call of 2026-09-28.
        Assert.DoesNotContain($"{Shell.ActionsKey}:", shell.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Project")]
    [InlineData("Deployment")]
    [InlineData("Help")]
    public void F10_and_the_arrows_reach_every_menu(string title)
    {
        // **The key pressed, not the seam.** Every test of the bar read its text and none pressed the
        // chord it advertised, so the chord did nothing on a real terminal and the suite was green.
        // CLAUDE.md has the sentence already -- a key tested only through its own seam is a key nobody
        // has pressed. Alt and a letter opened these until 2026-10-01; it is every action's key now, and
        // F10 with Left and Right is how the bar is reached (`KeysNeedAModifierTests`).
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        OpenMenu(driving, shell, title);

        Assert.Equal(title, shell.MenuOpen);
    }

    [Fact]
    public void Alt_and_a_menus_letter_no_longer_opens_it()
    {
        // The other half: Alt and H is an action's key now, and a menu that also answered it would be one keystroke
        // doing two things.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        driving.Press(Key.H.WithAlt);

        Assert.Null(shell.MenuOpen);
    }

    /// <summary>Opens a menu the way a person does: F10, then Right until it is the one wanted.</summary>
    /// <param name="driving">The session.</param>
    /// <param name="shell">The surface.</param>
    /// <param name="title">The menu's title.</param>
    internal static void OpenMenu(Driving.Session driving, Shell shell, string title)
    {
        driving.Press(Shell.MenuKey);

        for (var step = 0; step < 10 && shell.MenuOpen != title; step++)
        {
            driving.Press(Key.CursorRight);
        }

        Assert.Equal(title, shell.MenuOpen);
    }

    [Fact]
    public void A_menu_drops_down_under_the_bar_rather_than_opening_a_dialog()
    {
        // **The owner's call on 2026-09-27: a menu should be a menu.** 021 opened each one as a dialog in
        // the middle of the screen. Must fail against that: a dialog is a session on top of the shell, and
        // what drops down from a bar is drawn on the rows directly beneath it.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        OpenMenu(driving, shell, "Help");

        Assert.Same(shell, driving.App.TopRunnableView);

        var screen = driving.Text().ReplaceLineEndings("\n").Split('\n');

        Assert.Contains(Shell.KeysTitle, screen[1], StringComparison.Ordinal);
        Assert.Contains(Shell.AboutTitle, screen[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_line_runs_its_action_and_closes_the_menu()
    {
        // The whole path a person takes: Alt, a letter, an arrow, Enter. Down once is About, which opens a
        // dialog of its own -- so its title on screen is the action having run, not the menu still showing.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        OpenMenu(driving, shell, "Help");
        driving.Press(Key.CursorDown);
        driving.Enter();

        Assert.Null(shell.MenuOpen);
        Assert.True(driving.Has(Shell.AboutTitle), "choosing About did not open it");
    }

    [Fact]
    public async Task Escape_closes_a_menu_without_going_back()
    {
        // Escape means "back" everywhere on this surface, and a menu is on top of the view. Closing the menu
        // must be all it does -- leaving the project somebody was in because they dismissed a menu would be a
        // key doing two things at once.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Parley.Client.Views.Viewing.Project);
        shell.Lay(120);

        var before = shell.Place;

        // Help rather than Project: the bar is the focused region's, and in a project the agents pane has
        // focus and no project actions. Help is on every screen.
        OpenMenu(driving, shell, "Help");
        Assert.Equal("Help", shell.MenuOpen);

        driving.Escape();

        Assert.Null(shell.MenuOpen);
        Assert.Equal(before, shell.Place);
    }

    [Fact]
    public void Clicking_a_title_drops_its_menu_down()
    {
        // The mouse is the other half of what makes a bar a bar. The cell clicked is found by reading the
        // drawn row, not by repeating the arithmetic that placed the title there.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);
        driving.App.LayoutAndDraw(true);

        var top = driving.Text().ReplaceLineEndings("\n").Split('\n')[0];
        var column = top.IndexOf("Help", StringComparison.Ordinal);

        Assert.True(column >= 0, $"the top row reads: {top}");

        driving.ClickAt(new System.Drawing.Point(column + 1, 0));

        Assert.Equal("Help", shell.MenuOpen);
    }

    [Fact]
    public void Opening_menus_again_and_again_keeps_one_registered()
    {
        // Every open builds a new dropdown and registers it with the toolkit. Must fail against a bar that
        // forgets the last one: the toolkit would then hold a menu per keystroke for the life of the program,
        // each one offered every key somebody presses.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        for (var i = 0; i < 20; i++)
        {
            OpenMenu(driving, shell, "Help");
            driving.Escape();
        }

        Assert.True(
            driving.App.Popovers!.Popovers.Count <= 1,
            $"{driving.App.Popovers.Popovers.Count} menus are registered after twenty opens");
    }

    [Fact]
    public void Project_is_the_leftmost_menu()
    {
        // The owner's call on 2026-09-27: a person opens this surface to act on a project, so that is
        // the first thing the bar offers -- in the list and on the drawn row both.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);
        driving.App.LayoutAndDraw(true);

        Assert.Equal("Project", shell.BarTitles()[0]);

        var top = driving.Text().ReplaceLineEndings("\n").Split('\n')[0];

        Assert.True(
            top.IndexOf("Project", StringComparison.Ordinal) < top.IndexOf("Deployment", StringComparison.Ordinal),
            $"the top row reads: {top}");
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

        Assert.NotEmpty(shell.BarTitles());

        var screen = driving.Text().ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("Project", screen[0], StringComparison.Ordinal);
    }
}
