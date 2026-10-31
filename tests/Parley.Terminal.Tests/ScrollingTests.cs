using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a picker with more rows than fit scrolls, and still says what it is asking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner against a project with nineteen agents.</b> Choosing who to tell
/// something opened a list of twenty rows, and the cursor walked off the bottom of it: the
/// highlight — the only mark saying which row a key is about to act on — went off the screen and
/// the list never moved. The cause was arithmetic rather than the toolkit. The list was given
/// <c>Height = choices.Count</c>, so a list of twenty rows inside an eighteen-row dialog believed
/// every one of its rows was visible and had nothing to scroll.
/// </para>
/// <para>
/// <b>The same arithmetic hid the description.</b> The label took <c>Dim.Fill(choices.Count + 2)</c>
/// — whatever the list did not want — so a picker with twenty rows drew no description at all.
/// This surface's rule is that a choice sits behind a description of it, and
/// <c>DoorsAreReadableTests</c> could not see this because every dialog it drives offers two or
/// three choices.
/// </para>
/// <para>
/// <b>Read off the list's own viewport rather than off the screen, and that was learned here.</b>
/// The harness never pumps the main loop, so <c>Driving.Text()</c> returns whatever was last
/// drawn — a screen assertion passes or fails on when a redraw happened rather than on where the
/// cursor is. The viewport is what the toolkit itself indexes when it draws.
/// </para>
/// </remarks>
[Integration]
public sealed class ScrollingTests
{
    /// <summary>The owner's shape: every agent in the project, and the row that tells them all.</summary>
    private static IReadOnlyList<string> Nineteen()
    {
        var choices = new List<string> { "All agents (19)" };

        choices.AddRange(Enumerable.Range(1, 19).Select(at => $"agent-{at:00}@test"));

        return choices;
    }

    /// <summary>Whether the highlighted row is one the list is actually showing.</summary>
    private static bool OnScreen(ChoiceDialog dialog)
    {
        var at = dialog.Choices.SelectedItem ?? -1;
        var top = dialog.Choices.Viewport.Y;

        return at >= top && at < top + dialog.Choices.Viewport.Height;
    }

    [Theory]
    [InlineData(40)]
    [InlineData(30)]
    [InlineData(24)]
    public void The_highlighted_choice_stays_visible_all_the_way_down(int height)
    {
        using var driving = Driving.Open(width: 100, height: height);

        var choices = Nineteen();
        var dialog = new ChoiceDialog("Who are you speaking to?", "Tell somebody something", choices);

        driving.Show(dialog);

        for (var press = 0; press < choices.Count; press++)
        {
            dialog.Choices.NewKeyDownEvent(Key.CursorDown);

            Assert.True(
                OnScreen(dialog),
                $"row {dialog.Choices.SelectedItem} is outside {dialog.Choices.Viewport} "
                + $"on a {height}-row screen");
        }

        // And it reached the bottom of the roster, which is what somebody scrolling was after.
        Assert.Equal(choices[^1], dialog.Chosen);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(30)]
    [InlineData(24)]
    public void The_highlighted_choice_stays_visible_coming_back_up(int height)
    {
        using var driving = Driving.Open(width: 100, height: height);

        var choices = Nineteen();
        var dialog = new ChoiceDialog("Who are you speaking to?", "Tell somebody something", choices);

        driving.Show(dialog);

        for (var press = 0; press < choices.Count; press++)
        {
            dialog.Choices.NewKeyDownEvent(Key.CursorDown);
        }

        for (var press = 0; press < choices.Count; press++)
        {
            dialog.Choices.NewKeyDownEvent(Key.CursorUp);

            Assert.True(
                OnScreen(dialog),
                $"row {dialog.Choices.SelectedItem} is outside {dialog.Choices.Viewport} "
                + $"on a {height}-row screen");
        }

        Assert.Equal(choices[0], dialog.Chosen);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(24)]
    public void A_long_roster_does_not_squeeze_out_what_the_dialog_is_asking(int height)
    {
        using var driving = Driving.Open(width: 100, height: height);

        var dialog = new ChoiceDialog("Who are you speaking to?", "Tell somebody something", Nineteen());

        driving.Show(dialog);

        // The rule this surface is held to, at the size that used to break it: a choice sits
        // behind a description of it.
        Assert.Contains("Tell somebody something", driving.Text(), StringComparison.Ordinal);
    }
}
