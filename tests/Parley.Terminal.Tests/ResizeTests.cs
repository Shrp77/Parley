using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the surface stays coherent through a window being dragged about.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.9. Resizing crosses the layout threshold, and crossing it
/// reparents every region — so the invariants that have to survive are the ones a person would
/// notice losing: which region has focus, that exactly one region is visible when stacked and all
/// four when not, and that a dialog opened before the drag is still the thing taking keys after it.
/// </para>
/// <para>
/// A drag produces a great many resizes rather than one, so the same crossing is made repeatedly
/// rather than once.
/// </para>
/// </remarks>
public sealed class ResizeTests
{
    [Fact]
    public async Task Focus_survives_crossing_the_threshold_in_both_directions()
    {
        using var driving = Driving.Open(width: Shell.Threshold + 40, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        driving.Tab();

        var before = shell.FocusedRegion;

        for (var drag = 0; drag < 5; drag++)
        {
            shell.Lay(Shell.Threshold - 1);
            shell.Lay(Shell.Threshold);
        }

        Assert.Equal(before, shell.FocusedRegion);
        Assert.Contains(
            ConversationsRegion.RegionTitle, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_view_survives_crossing_the_threshold_in_both_directions()
    {
        // Added by 006. Focus surviving a drag is not the same as the view surviving one, and a
        // resize that quietly returned somebody to the projects list would lose their place in a
        // way no assertion about focus could see.
        using var driving = Driving.Open(width: Shell.Threshold + 40, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        for (var drag = 0; drag < 5; drag++)
        {
            shell.Lay(Shell.Threshold - 1);
            shell.Lay(Shell.Threshold);
        }

        Assert.Equal(Viewing.Conversation, shell.Place.Viewing);
        Assert.Equal("some-discussion", shell.Place.Conversation);
    }

    [Fact]
    public async Task Exactly_one_pane_is_visible_when_stacked_and_both_when_not()
    {
        using var driving = Driving.Open(width: Shell.Threshold + 40, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Lay(Shell.Threshold - 40);

        Assert.Equal(1, shell.Panes.Count(pane => pane.Visible));

        shell.Lay(Shell.Threshold + 40);

        Assert.All(shell.Panes, pane => Assert.True(pane.Visible));
    }

    [Fact]
    public async Task A_view_the_person_is_not_on_puts_nothing_on_screen()
    {
        // The half of "one view at a time" that a visibility toggle gets wrong in the other
        // direction: a region left visible from the previous view draws over the current one.
        using var driving = Driving.Open(width: Shell.Threshold + 40, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);
        driving.App.LayoutAndDraw(true);

        Assert.False(shell.Projects.Visible);
        Assert.False(shell.Agents.Visible);
        Assert.False(shell.Conversations.Visible);
        Assert.True(shell.Transcript.Visible);
    }

    [Fact]
    public async Task Tabbing_while_stacked_shows_the_pane_it_moved_to()
    {
        // The half a visibility toggle gets wrong: focus moves and the screen does not follow, so
        // the status line names a pane nobody can see.
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        shell.Lay(Shell.Threshold - 40);

        foreach (var expected in new global::Terminal.Gui.ViewBase.View[]
                 {
                     shell.Conversations, shell.Agents,
                 })
        {
            driving.Tab();

            Assert.True(expected.Visible, "the pane Tab moved to is not the one on screen");
        }
    }

    [Fact]
    public void A_dialog_opened_before_a_drag_is_still_the_thing_taking_keys_after_it()
    {
        using var driving = Driving.Open(width: Shell.Threshold + 40, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var dialog = Prompting.Text("Something", "Type here:");
        var token = Prompting.Open(driving.App, dialog);

        shell.Lay(Shell.Threshold - 40);
        shell.Lay(Shell.Threshold + 40);

        driving.Type("still here");

        Assert.Equal("still here", dialog.Typed);

        Prompting.Close(driving.App, token);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
