using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a message is shown whole, and can be taken off the screen when it has been read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's second report.</b> They told nineteen agents something, every one was refused,
/// and what they saw was <c>Told 0 of 19. Refused for 19: nothing has registered under that name
/// yet, so nothing can be addressed to it. Stating it once is what</c> — cut off mid-sentence, and
/// cut off inside the half that says what to do about it. The line was one row and a broadcast
/// report is several.
/// </para>
/// <para>
/// <b>Two halves, and the second was asked for in the same breath:</b> a report that has been read
/// is occupying rows somebody wants back. Nothing clears it on a timer — a line that faded would
/// take the only record of what happened with it — so there is a key, and it is on the status line
/// and in the menu like every other action here.
/// </para>
/// </remarks>
public sealed class MessageLineGrowsTests
{
    /// <summary>A screen the size of somebody's terminal rather than the harness's default.</summary>
    /// <remarks>
    /// The default is two hundred and forty columns wide, where a broadcast report to four agents
    /// fits on one row and this whole defect is invisible. The owner's was a window.
    /// </remarks>
    private const int Columns = 100;

    /// <summary>How tall that screen is.</summary>
    private const int Tall = 30;

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    /// <summary>A report the shape of the one the owner met, at whatever length is wanted.</summary>
    private static string Report(int names) =>
        "Told 0 of " + names + ". Refused for " + names
        + ": nothing has registered under that name yet, so nothing can be addressed to it. "
        + "Starting it once is what registers it ("
        + string.Join(", ", Enumerable.Range(0, names).Select(at => $"agent-{at}@machine"))
        + ").";

    [Fact]
    public void A_report_too_long_for_one_row_is_drawn_on_several()
    {
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var said = Report(4);

        shell.Report(said);

        driving.App.LayoutAndDraw(true);

        // Every word of it, including the sentence the owner never saw.
        Assert.True(shell.Shown.Count > 1, "the message is still on one row");
        Assert.False(shell.SaidMore);
        Assert.Equal(said, string.Join(" ", shell.Shown).Replace("  ", " ", StringComparison.Ordinal));
        Assert.Contains("Starting it once is what registers it", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_regions_give_up_exactly_the_rows_the_message_takes()
    {
        // The panes are sized from the message rather than from a constant, and a message that
        // grew over them would be a surface that hid what somebody opened it to read.
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var before = shell.Projects.Frame.Height;

        shell.Report(Report(4));

        driving.App.LayoutAndDraw(true);

        var rows = shell.Shown.Count;

        Assert.True(rows > 1);
        Assert.Equal(before - (rows - 1), shell.Projects.Frame.Height);

        // And gives them back.
        _ = shell.Clear();

        driving.App.LayoutAndDraw(true);

        Assert.Equal(before, shell.Projects.Frame.Height);
        Assert.Equal(string.Empty, shell.Said);
    }

    [Fact]
    public void A_message_past_the_bound_says_so_and_is_readable_whole()
    {
        // Bounded, because a screen is one screen -- and it says it is bounding rather than
        // cutting silently, which is the rule this surface is built on.
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var said = Report(40);

        shell.Report(said);

        driving.App.LayoutAndDraw(true);

        Assert.True(shell.SaidMore);
        Assert.True(shell.Shown.Count <= Shell.MessageDeepest);
        Assert.Contains(Shell.WholeTitle, shell.Shown[^1], StringComparison.Ordinal);

        // Pressed rather than called, because the key is what the last row advertises.
        driving.Press(new Key(Shell.WholeKey));
        driving.App.LayoutAndDraw(true);

        Assert.NotNull(shell.Showing);
        Assert.True(driving.Has(Saying.Heading), "the whole message is not on screen");

        // Whole means whole: every word is in the dialog, wrapped and scrollable.
        Assert.Equal(
            said.Replace(" ", string.Empty, StringComparison.Ordinal),
            string.Concat(shell.Showing!.Shown).Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void On_a_short_terminal_the_regions_keep_their_floor()
    {
        // The bound is whichever is smaller: a paragraph, or what is left after the regions have
        // their floor. A message free to take a fourteen-row window would leave a surface showing
        // nothing but its own last sentence.
        using var driving = Driving.Open(Columns, Shell.LeastRegionRows + 4);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Report(Report(40));

        driving.App.LayoutAndDraw(true);

        Assert.True(
            shell.Shown.Count <= 3,
            $"the message took {shell.Shown.Count} rows of a {Shell.LeastRegionRows + 4}-row screen");

        Assert.True(shell.SaidMore);
        Assert.True(shell.Projects.Frame.Height >= Shell.LeastRegionRows);
    }

    [Fact]
    public void The_key_that_clears_it_is_on_the_status_line_and_in_the_menu()
    {
        // contracts/surface.md: no action exists only as a key. The menu is also the only route a
        // pointer has to one.
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        Assert.Empty(shell.Messaging());

        shell.Report("something happened");

        driving.App.LayoutAndDraw(true);

        Assert.Contains(Shell.ClearTitle, shell.Status, StringComparison.Ordinal);

        _ = shell.Offer();

        var menu = (ChoiceDialog)driving.Find(Shell.ActionsTitle);

        Assert.Contains(
            Drawn.Rows(menu.Choices).Select(row => row.Trim()),
            row => row.Contains(Shell.ClearTitle, StringComparison.Ordinal));
    }

    [Fact]
    public void Pressing_the_key_clears_it()
    {
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Report("something happened");

        driving.App.LayoutAndDraw(true);

        Assert.Contains("something happened", driving.Text(), StringComparison.Ordinal);

        driving.Press(new Key(Shell.ClearKey));
        driving.App.LayoutAndDraw(true);

        Assert.Equal(string.Empty, shell.Said);
        Assert.DoesNotContain("something happened", driving.Text(), StringComparison.Ordinal);

        // And the action stops being offered once there is nothing to act on, so the status line
        // never names a key that would do nothing.
        Assert.Empty(shell.Messaging());
        Assert.DoesNotContain(Shell.ClearTitle, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_regions_own_letter_still_wins()
    {
        // The message's keys are global and are taken after a region's, so a pane that grows an
        // action bound to the same letter keeps it. Checked on the projects view, where `b` opens
        // the console -- and would be shadowed by a global if the order were the other way round.
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Report("something happened");

        var letters = shell.Actions().Select(action => action.Key).ToList();

        Assert.DoesNotContain(Shell.ClearKey, letters);
        Assert.DoesNotContain(Shell.WholeKey, letters);
    }
}
