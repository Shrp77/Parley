using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the way out of a view is on the screen, and not only under a key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from a report, and the report was about a key that already worked.</b> Escape has
/// gone back since 006 was built and <c>NavigatingTests</c> proves it; the owner still walked into
/// a project and said there was no way back to the list. Both are true at once, which is the whole
/// finding: the surface answered a key nothing on it named.
/// </para>
/// <para>
/// So none of these press Escape — that is already covered, and covering it again would be a
/// second test of the half that was never broken. What is asserted here is that somebody who does
/// not know the key can find it: it is written on the status line of every view that has one, and
/// it is an entry in the menu the pointer reaches, including on the one view where a region offers
/// no actions at all and the menu used to refuse to open.
/// </para>
/// </remarks>
public sealed class WayBackTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task The_status_line_of_a_project_says_how_to_get_back_to_the_projects()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Contains(Shell.BackStroke, shell.Status, StringComparison.Ordinal);
        Assert.Contains(ViewPlace.Top.Title, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_names_where_it_leads_rather_than_only_that_it_goes_back()
    {
        // One step out of a conversation is the project, not the top. A person three levels down
        // who reads "back" alone cannot tell one keystroke from two, and the difference is whether
        // they lose the conversation they were reading.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        Assert.Contains(Walking.Alpha.Value, shell.BackLabel ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_projects_view_offers_no_way_back_because_there_is_nowhere_to_go()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Projects);

        Assert.Null(shell.BackLabel);
        Assert.DoesNotContain(Shell.BackStroke, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_pane_below_the_top_says_it_whichever_one_has_focus()
    {
        // The status line is rebuilt on every change of focus, so an affordance added to one
        // branch of that rebuild and not another would be there in the agents pane and gone in the
        // conversations pane — which is the shape of the defect this replaces, one region deeper.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var checked_ = 0;

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            if (shell.Place.Viewing == Viewing.Projects)
            {
                continue;
            }

            Assert.Contains(Shell.BackStroke, shell.Status, StringComparison.Ordinal);
            checked_++;
        }

        // Non-vacuity: a walk that yielded nothing would make the loop above pass by never running,
        // which is the shape this repository has been bitten by more than once.
        Assert.True(checked_ > 0, "no pane below the top was visited, so nothing was checked");
    }

    [Fact]
    public async Task It_survives_the_status_line_running_out_of_room_for_the_actions()
    {
        // The actions collapse to "?: actions" when they do not fit, and back outlives them by a
        // rung: a person short of width is more likely to be lost than to want the sixth action.
        using var driving = Driving.Open(width: Shell.Threshold, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(Shell.Threshold);
        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Contains($"{Shell.ActionsKey}: actions", shell.Status, StringComparison.Ordinal);
        Assert.Contains(Shell.BackStroke, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Narrower_still_it_is_in_the_menu_rather_than_on_the_line()
    {
        // **The honest end of the ladder, written down rather than left to be discovered.** Forty
        // columns cannot hold a place, a focus, a way back, a way out and a list of actions, and
        // something has to go. What goes is the pair that the `?` menu also holds, so the short
        // form is the same way out one keystroke further away — not a screen with none.
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(40);
        await Walking.GoAsync(shell, Viewing.Project);
        shell.Lay(40);

        Assert.True(shell.Stacked, "the surface should have stacked below the threshold");
        Assert.DoesNotContain(Shell.BackStroke, shell.Status, StringComparison.Ordinal);
        Assert.Contains($"{Shell.ActionsKey}:", shell.Status, StringComparison.Ordinal);

        // And the menu it points at still has it, which is the half that makes the omission safe.
        await shell.Offer();

        var menu = Assert.IsType<ChoiceDialog>(driving.Find(Shell.ActionsTitle));

        Assert.Contains(
            Drawn.Rows(menu.Choices),
            row => row.Contains(Shell.BackStroke, StringComparison.Ordinal));
        Assert.Contains(Drawn.Rows(menu.Choices), row => row == Shell.QuitLabel);
    }

}
