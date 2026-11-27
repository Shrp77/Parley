using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That no plain character is an action anywhere on this surface, and that Alt and the character is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-01</b>: typing into <i>Say something</i>, they pressed <c>c</c> and the conversation
/// went to the clipboard. Every action was a bare letter, and the only thing standing between typing and an action was
/// the box's guard, which holds while the field itself has focus and not while focus has moved off it within its pane.
/// The owner's call was that actions take a modifier: Alt, with the menus moved to F10 to free it.
/// </para>
/// <para>
/// <b>Every pane, every letter.</b> One pane and one letter would hold the reported case and nothing else; the property
/// is that no plain character is an action wherever the cursor is, and an action added next month on a pane nobody
/// thought of is held to it too.
/// </para>
/// </remarks>
[Integration]
public sealed class KeysNeedAModifierTests
{
    private const string Plain = "abcdefghijklmnopqrstuvwxyz?";

    [Fact]
    public async Task A_plain_letter_on_any_pane_of_a_conversation_runs_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        var shell = seated.Shell;

        for (var pane = 0; pane < shell.Panes.Count; pane++)
        {
            shell.Focus(pane);

            var said = shell.Said;

            foreach (var character in Plain)
            {
                seated.Session.Press(new Key(character));
            }

            await shell.Settled;

            var where = shell.Panes[pane].GetType().Name;

            Assert.Empty(seated.Session.Copied);
            Assert.Same(shell, seated.Session.App.TopRunnableView);
            Assert.Null(shell.MenuOpen);
            Assert.False(shell.Transcript.Folded, $"a plain f folded the transcript from {where}");
            Assert.Equal(said, shell.Said);

            shell.Line.Written = string.Empty;
        }
    }

    [Fact]
    public async Task Alt_and_the_letter_runs_the_action_even_while_typing()
    {
        // The half that makes the modifier worth having: an action is reachable from the box without leaving it.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        var shell = seated.Shell;

        // The actions list rather than Copy: Copy is the transcript's own action and is not offered from the box's pane,
        // while the actions list is offered everywhere.
        seated.Focus<LineRegion>();
        seated.Session.Type("half a sentence");
        seated.Session.Press(new Key(Shell.ActionsKey).WithAlt);

        Assert.True(seated.Session.Has(Shell.ActionsTitle), "Alt+? did not open the actions list from the box");
        Assert.Equal("half a sentence", shell.Line.Written);
    }

    [Fact]
    public async Task Every_action_is_written_with_its_modifier()
    {
        // What the status line and the menus say is what a person presses, so the spelling is held as well as the
        // dispatch. A chord is an action's own key (F2, Enter, the arrows) and is written as itself.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);

        await foreach (var _ in Walking.EveryPaneAsync(seated.Shell))
        {
            Assert.All(
                seated.Shell.Actions(),
                action => Assert.True(
                    action.Chord is not null || action.Stroke == Shell.Alt(action.Key),
                    $"'{action.Label}' is written '{action.Stroke}'"));

            Assert.All(
                seated.Shell.Actions().Where(action => action.Chord is null),
                action => Assert.False(action.Answers(new Key(action.Key)), $"'{action.Label}' answers a plain key"));
        }
    }

    [Fact]
    public void F10_opens_the_first_menu_and_the_arrows_walk_the_bar()
    {
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = MenuBarOnScreenTests.Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(120);

        // The key is named on the bar itself, so the menus are not reachable only by somebody who already knows.
        Assert.Contains(MenuBarView.MenusHint, driving.Text().ReplaceLineEndings("\n").Split('\n')[0], StringComparison.Ordinal);

        driving.Press(Shell.MenuKey);

        var first = shell.MenuOpen;

        Assert.NotNull(first);

        driving.Press(Key.CursorRight);

        Assert.NotNull(shell.MenuOpen);
        Assert.NotEqual(first, shell.MenuOpen);

        driving.Press(Key.CursorLeft);

        Assert.Equal(first, shell.MenuOpen);
    }
}
