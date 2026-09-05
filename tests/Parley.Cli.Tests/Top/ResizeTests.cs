using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

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
    public void Focus_survives_crossing_the_threshold_in_both_directions()
    {
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        driving.Tab();
        driving.Tab();

        var before = shell.FocusedRegion;

        for (var drag = 0; drag < 5; drag++)
        {
            shell.Lay(Shell.Threshold - 1);
            shell.Lay(Shell.Threshold);
        }

        Assert.Equal(before, shell.FocusedRegion);
        Assert.StartsWith(
            Shell.FocusPrefix + ConversationsRegion.RegionTitle,
            shell.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_one_region_is_visible_when_stacked_and_all_four_when_not()
    {
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Lay(40);

        var visible = new Terminal.Gui.ViewBase.View[]
            {
                shell.Projects, shell.Agents, shell.Conversations, shell.Transcript,
            }
            .Count(region => region.Visible);

        Assert.Equal(1, visible);

        shell.Lay(120);

        Assert.All(
            new Terminal.Gui.ViewBase.View[]
            {
                shell.Projects, shell.Agents, shell.Conversations, shell.Transcript,
            },
            region => Assert.True(region.Visible));
    }

    [Fact]
    public void Tabbing_while_stacked_shows_the_region_it_moved_to()
    {
        // The half a visibility toggle gets wrong: focus moves and the screen does not follow, so
        // the status line names a region nobody can see.
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(40);

        foreach (var expected in new Terminal.Gui.ViewBase.View[]
                 {
                     shell.Agents, shell.Conversations, shell.Transcript,
                 })
        {
            driving.Tab();

            Assert.True(expected.Visible, "the region Tab moved to is not the one on screen");
        }
    }

    [Fact]
    public void A_dialog_opened_before_a_drag_is_still_the_thing_taking_keys_after_it()
    {
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var dialog = Prompting.Text("Something", "Type here:");
        var token = Prompting.Open(driving.App, dialog);

        shell.Lay(40);
        shell.Lay(120);

        driving.Type("still here");

        Assert.Equal("still here", dialog.Typed);

        Prompting.Close(driving.App, token);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
