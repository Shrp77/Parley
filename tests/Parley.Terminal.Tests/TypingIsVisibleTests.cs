using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That what somebody types into the box is on the screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other test of this box reads what was written; none of them looked at the screen.</b> So a box
/// with no rows at all passed all of them: the text went in, the completion list opened on <c>@</c>, and the
/// owner had no cursor and saw nothing they typed. A closed completion list was still reserving the row the
/// box was to be drawn in, and <c>Dim.Fill()</c> gave it what was left, which was nothing.
/// </para>
/// <para>
/// This repository has recorded that shape three times before — <c>Shell.Said</c> computed and shown to
/// nobody, a terminal with no colour under a passing <c>ColourTests</c>, and the line pane with no size at
/// all. **A property the suite holds to and the screen never shows.** This file is the screen.
/// </para>
/// </remarks>
public sealed class TypingIsVisibleTests
{
    [Fact]
    public async Task The_box_has_rows_to_draw_in()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);

        seated.Lay();
        seated.Focus<LineRegion>();

        // Typed first, because the preview is what pushes the box down: with nothing written the region is
        // short enough that a row was left over anyway, and this passed against the defect it is named for.
        seated.Shell.NewKeyDownEvent(new Key('a'));

        await seated.Shell.Settled;

        seated.Lay();

        Assert.True(
            seated.Shell.Line.BoxRows > 0,
            $"the box got {seated.Shell.Line.BoxRows} rows of the region's {seated.Shell.Line.Rows}");
    }

    [Fact]
    public async Task What_is_typed_is_drawn()
    {
        // Read off the drawn screen rather than out of the field, because the field was right the whole time.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<LineRegion>();

        foreach (var key in "hello there")
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;
        seated.Lay();

        Assert.Contains("hello there", seated.Session.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task And_still_has_rows_while_the_completion_list_is_open()
    {
        // The list takes its rows from the region, which asks for more; what it must never take is the box's.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<LineRegion>();

        foreach (var key in "@ord")
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;
        seated.Lay();

        Assert.NotEmpty(shell.Line.Offering);
        Assert.True(shell.Line.BoxRows > 0, $"the box got {shell.Line.BoxRows} rows with the list open");
        Assert.Contains("@ord", seated.Session.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_of_several_lines_is_drawn_whole()
    {
        // The owner's report that started this: a long message must not slide out of sight sideways.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<LineRegion>();

        var before = shell.Line.BoxRows;

        shell.Line.Written = new string('a', 400);

        await shell.Settled;
        seated.Lay();

        Assert.True(
            shell.Line.BoxRows > before,
            $"the box stayed at {shell.Line.BoxRows} rows for four hundred characters");
    }
}
