using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What each keystroke does to a repository picker, checked without a terminal.
/// </summary>
/// <remarks>
/// <para>
/// The picker exists because enrolling is opt-in and a directory of twenty checkouts makes
/// opting in by hand tedious enough that somebody will reach for <c>--all</c> instead. Its
/// defects are all in what a key does to the selection rather than in reading the key, so the
/// state machine is a pure function and this drives it with synthetic keystrokes.
/// </para>
/// <para>
/// The case that matters most is the last one: an unrecognised key must change nothing. A picker
/// that treats an unexpected key as confirmation enrolls repositories because somebody leaned on
/// the keyboard, which defeats the point of the screen existing.
/// </para>
/// </remarks>
[Integration]
public sealed class RepositorySelectionTests
{
    private static RepositorySelection Three(params bool[] selected) =>
        RepositorySelection.Over(
        [
            new SelectableRepository("Orders", "/srv/orders", string.Empty, selected.ElementAtOrDefault(0)),
            new SelectableRepository("Billing", "/srv/billing", "already an agent", selected.ElementAtOrDefault(1)),
            new SelectableRepository("Stock", "/srv/stock", string.Empty, selected.ElementAtOrDefault(2)),
        ]);

    private static RepositorySelection Press(RepositorySelection state, params ConsoleKey[] keys) =>
        keys.Aggregate(state, (current, key) => current.Press(key));

    [Fact]
    public void Nothing_is_selected_to_begin_with()
    {
        // The default the whole field exists for: a scan finds repositories, it does not agree to
        // enroll them.
        Assert.Equal(0, Three().SelectedCount);
        Assert.Equal(SelectionStatus.Choosing, Three().Status);
    }

    [Fact]
    public void Space_ticks_the_row_the_cursor_is_on()
    {
        var state = Press(Three(), ConsoleKey.DownArrow, ConsoleKey.Spacebar);

        Assert.False(state.Repositories[0].Selected);
        Assert.True(state.Repositories[1].Selected);
        Assert.Equal(1, state.SelectedCount);
    }

    [Fact]
    public void Space_twice_leaves_the_row_as_it_was()
    {
        Assert.Equal(0, Press(Three(), ConsoleKey.Spacebar, ConsoleKey.Spacebar).SelectedCount);
    }

    [Fact]
    public void The_cursor_stops_at_the_ends_rather_than_wrapping()
    {
        // A list that jumps from the bottom to the top is how somebody ticks the first repository
        // while believing they ticked the last.
        Assert.Equal(0, Press(Three(), ConsoleKey.UpArrow, ConsoleKey.UpArrow).Cursor);
        Assert.Equal(
            2,
            Press(Three(), ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.DownArrow).Cursor);
    }

    [Fact]
    public void A_selects_everything_and_n_selects_nothing()
    {
        var all = Press(Three(), ConsoleKey.A);

        Assert.Equal(3, all.SelectedCount);
        Assert.Equal(0, Press(all, ConsoleKey.N).SelectedCount);
    }

    [Fact]
    public void Enter_confirms_and_escape_cancels()
    {
        Assert.Equal(SelectionStatus.Confirmed, Press(Three(), ConsoleKey.Enter).Status);
        Assert.Equal(SelectionStatus.Cancelled, Press(Three(), ConsoleKey.Escape).Status);
    }

    [Fact]
    public void Cancelling_keeps_the_selection_so_the_caller_can_tell_them_apart()
    {
        // The caller discards on Cancelled rather than reading the ticks, and this is what makes
        // that distinction possible: a cancelled picker with two ticks must not look like a
        // confirmed one.
        var state = Press(Three(), ConsoleKey.Spacebar, ConsoleKey.Escape);

        Assert.Equal(SelectionStatus.Cancelled, state.Status);
        Assert.Equal(1, state.SelectedCount);
    }

    [Theory]
    [InlineData(ConsoleKey.F5)]
    [InlineData(ConsoleKey.Backspace)]
    [InlineData(ConsoleKey.Tab)]
    [InlineData(ConsoleKey.PageDown)]
    public void An_unrecognised_key_changes_nothing(ConsoleKey key)
    {
        var before = Three(true, false, false);
        var after = before.Press(key);

        Assert.Equal(SelectionStatus.Choosing, after.Status);
        Assert.Equal(before.Cursor, after.Cursor);
        Assert.Equal(before.SelectedCount, after.SelectedCount);
    }

    [Fact]
    public void The_rendering_marks_the_cursor_the_ticks_and_the_count()
    {
        var lines = Press(Three(), ConsoleKey.Spacebar, ConsoleKey.DownArrow).Render();
        var text = string.Join('\n', lines);

        Assert.Contains("  [x] Orders", text, StringComparison.Ordinal);
        Assert.Contains("> [ ] Billing", text, StringComparison.Ordinal);
        Assert.Contains("1 of 3 selected.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_that_is_already_an_agent_says_so()
    {
        // The row somebody most needs to recognise in a list of twenty: leaving it unticked is
        // how a working agent silently stops being offered.
        Assert.Contains(
            "already an agent",
            string.Join('\n', Three().Render()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_row_is_truncated_rather_than_wrapped()
    {
        var selection = RepositorySelection.Over(
            [new SelectableRepository(new string('x', 200), "/srv/x", string.Empty, false)]);

        Assert.All(selection.Render(40), line => Assert.True(line.Length <= 40));
    }

    [Fact]
    public void A_picker_over_nothing_is_refused()
    {
        // Reached by scanning a directory of checkouts that all turned out not to be git
        // repositories. An empty picker is a screen with no way out of it.
        Assert.Throws<ArgumentException>(() => RepositorySelection.Over([]));
    }
}
