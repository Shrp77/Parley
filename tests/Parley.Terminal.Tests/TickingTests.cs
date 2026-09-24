using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The list that takes several answers, which enrolling a folder is the only thing here that needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every key is pressed rather than driven through the method it calls.</b> `Lists` has recorded
/// the sentence since 0.20.3 and 0.30.4 paid for it again: a key tested only through its own seam is
/// a key nobody has pressed, and the one thing that seam exists to check is that the key arrives at
/// all. `Toggle` and `SetAll` are reachable directly and are deliberately not used here.
/// </para>
/// <para>
/// This is also what keeps `SetAll` internal rather than private. `DeadSurfaceTests` reported it as
/// named only in its own file, which was true and had two answers: make it private, or press the
/// keys that reach it. The second is worth more, because a private method reached by a key nothing
/// presses is still a key nothing presses.
/// </para>
/// </remarks>
public sealed class TickingTests
{
    private static TickDialog Over(params string[] labels) =>
        new(
            "Enrolment",
            "Tick the ones that should become agents.",
            [.. labels.Select(label => new Tickable(label, string.Empty, Ticked: false))]);

    private static IReadOnlyList<string> Ticked(TickDialog dialog) =>
        [.. dialog.Items.Where(item => item.Ticked).Select(item => item.Label)];

    [Fact]
    public void Nothing_is_ticked_to_begin_with()
    {
        // A pre-ticked list is a default that enrols whatever somebody did not notice, and this
        // dialog exists for lists too long to take in at once.
        var dialog = Over("orders", "billing", "inventory");

        Assert.Empty(Ticked(dialog));
    }

    [Fact]
    public void Space_ticks_the_row_the_cursor_is_on()
    {
        var dialog = Over("orders", "billing", "inventory");

        dialog.Rows.SelectedItem = 1;
        dialog.NewKeyDownEvent(Key.Space);

        Assert.Equal(["billing"], Ticked(dialog));
    }

    [Fact]
    public void Space_twice_leaves_the_row_as_it_was()
    {
        var dialog = Over("orders", "billing");

        dialog.Rows.SelectedItem = 0;
        dialog.NewKeyDownEvent(Key.Space);
        dialog.NewKeyDownEvent(Key.Space);

        Assert.Empty(Ticked(dialog));
    }

    [Fact]
    public void A_ticks_everything_and_n_ticks_nothing()
    {
        var dialog = Over("orders", "billing", "inventory");

        dialog.NewKeyDownEvent(Key.A.WithShift.NoShift);

        Assert.Equal(["orders", "billing", "inventory"], Ticked(dialog));

        dialog.NewKeyDownEvent(Key.N.WithShift.NoShift);

        Assert.Empty(Ticked(dialog));
    }

    [Fact]
    public void Enter_hands_back_what_was_ticked()
    {
        var dialog = Over("orders", "billing");
        IReadOnlyList<Tickable>? handed = null;
        var raised = 0;

        dialog.Done += (_, chosen) =>
        {
            handed = chosen;
            raised++;
        };

        dialog.Rows.SelectedItem = 0;
        dialog.NewKeyDownEvent(Key.Space);
        dialog.NewKeyDownEvent(Key.Enter);

        Assert.Equal(1, raised);
        Assert.NotNull(handed);
        Assert.Equal(["orders"], handed.Where(item => item.Ticked).Select(item => item.Label));
    }

    [Fact]
    public void Escape_is_not_the_same_as_ticking_nothing()
    {
        // The distinction the caller acts on: somebody who pressed Escape has not said "enrol none
        // of them", they have said "not now" — and enrolling nothing is a sentence worth printing
        // while cancelling is not.
        var dialog = Over("orders", "billing");
        var raised = 0;
        IReadOnlyList<Tickable>? handed = [];

        dialog.Done += (_, chosen) =>
        {
            handed = chosen;
            raised++;
        };

        dialog.NewKeyDownEvent(Key.Esc);

        Assert.Equal(1, raised);
        Assert.Null(handed);
    }

    [Fact]
    public void The_rows_carry_the_tick_as_text_rather_than_as_a_colour()
    {
        // Every mark on this surface has to survive the attributes being stripped: the suite reads
        // the screen that way, and a state that exists only as a colour is one nothing can assert
        // and a colour-blind reader cannot see. 0.18.4 records the terminal having no colour at all
        // under a passing ColourTests, which is the same lesson from the other side.
        var dialog = Over("orders", "billing");

        dialog.Rows.SelectedItem = 0;
        dialog.NewKeyDownEvent(Key.Space);

        var drawn = dialog.Rows.Source!.ToList();

        Assert.Contains("[x] orders", drawn[0]?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[ ] billing", drawn[1]?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_is_shown_beside_the_row_it_belongs_to()
    {
        // The note is what a person decides on — that a checkout is a worktree sharing its
        // .mcp.json, or that it already says what it owns. A picker that showed only names would
        // make the decision blind.
        var dialog = new TickDialog(
            "Enrolment",
            "Tick the ones that should become agents.",
            [new Tickable("orders", "owns: carts and totals", Ticked: false)]);

        var drawn = dialog.Rows.Source!.ToList()[0]?.ToString() ?? string.Empty;

        Assert.Contains("owns: carts and totals", drawn, StringComparison.Ordinal);
    }
}
