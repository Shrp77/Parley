using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a view's panes stack rather than truncate when the terminal is narrow, and that Tab
/// still visits every one of them.
/// </summary>
/// <remarks>
/// <para>
/// The threshold is driven at both sides rather than trusted, because 004's narrow-terminal defect
/// — a recipient column silently dropped — was found by a width test and not by looking at the
/// code. A layout that rearranges itself has two behaviours and a suite that only ever sees one of
/// them is testing half a surface.
/// </para>
/// <para>
/// Every assertion here finds a region by its title, never by where it is. That is the rule the
/// whole harness is built on and this is the test that would break first if it were abandoned: the
/// regions are in different places in the two layouts and the same test passes in both.
/// </para>
/// <para>
/// <b>Driven on the project view, because it is the only one with two panes.</b> 006 replaced four
/// regions on one screen with three views of one or two, so the threshold now decides whether a
/// project's agents and conversations sit side by side or one at a time. A one-pane view fills the
/// width at every size and has nothing to stack.
/// </para>
/// </remarks>
public sealed class LayoutTests
{
    [Fact]
    public async Task At_the_threshold_a_projects_two_panes_are_side_by_side()
    {
        using var driving = Driving.Open(width: Shell.Threshold, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        shell.Lay(Shell.Threshold);
        driving.App.LayoutAndDraw(true);

        Assert.False(shell.Stacked);
        Assert.Contains(AgentsRegion.RegionTitle, driving.Text(), StringComparison.Ordinal);
        Assert.Contains(ConversationsRegion.RegionTitle, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void One_column_below_the_threshold_they_stack()
    {
        using var driving = Driving.Open(width: Shell.Threshold - 1, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(Shell.Threshold - 1);
        driving.App.LayoutAndDraw(true);

        Assert.True(shell.Stacked);
    }

    [Fact]
    public async Task Tab_still_visits_both_panes_when_they_are_stacked()
    {
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        shell.Lay(40);

        var visited = new List<string>();

        for (var step = 0; step < 2; step++)
        {
            visited.Add(shell.Status);
            driving.Tab();
        }

        // Each in turn, and each named. The status line leads with where in the deployment the
        // person is, so the pane's own name is asserted as present rather than as the first thing.
        Assert.Collection(
            visited,
            said => Assert.Contains(AgentsRegion.RegionTitle, said, StringComparison.Ordinal),
            said => Assert.Contains(ConversationsRegion.RegionTitle, said, StringComparison.Ordinal));
    }

    [Fact]
    public async Task At_forty_columns_each_pane_names_itself_in_full_as_it_is_reached()
    {
        // Scenario 5, and the assertion is deliberately about full names rather than about all
        // four being visible at once. Four titles do not fit across forty columns and the toolkit's
        // answer to that is to cut them: the first version of this layout used its tab strip and
        // drew `Conversat`, which is exactly the silent truncation the specification forbids. One
        // region at a time, named in full, is what replaced it — the fact 004 lost was not "how
        // many regions exist" but "what am I looking at".
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        shell.Lay(40);

        foreach (var title in new[]
        {
            AgentsRegion.RegionTitle,
            ConversationsRegion.RegionTitle,
        })
        {
            driving.App.LayoutAndDraw(true);

            var screen = driving.Text();

            Assert.Contains(title, screen, StringComparison.Ordinal);
            Assert.Contains(title, shell.Status, StringComparison.Ordinal);
            Assert.DoesNotContain("…", screen, StringComparison.Ordinal);

            driving.Tab();
        }
    }

    [Fact]
    public async Task The_stacked_status_line_fits_the_narrowest_terminal_it_supports()
    {
        // A status line clipped mid-word would be the same silent truncation this layout exists to
        // avoid, arrived at from the other direction.
        //
        // **Every pane of every view, because it used to drive only the projects view.** That is
        // the one view with no place prefix and no way-back label, so it is the shortest line the
        // surface ever draws — and the guard sat on it while a project view at forty columns was
        // over the width before an action was named. It went unnoticed until Ctrl+Q pushed the top
        // view one column over too, which is the whole argument for driving all of them.
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(40);

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            shell.Lay(40);

            Assert.True(
                shell.Status.Length <= 40,
                $"on {shell.Place.Viewing} the status line is "
                + $"{shell.Status.Length} columns: '{shell.Status}'");
        }
    }

    [Fact]
    public async Task The_narrowest_status_line_still_says_which_pane_and_how_to_see_the_rest()
    {
        // What the ladder must never cut. Which pane has focus is the thing Tab acts on. The way to
        // everything the line no longer has room to name -- including both ways out -- is the menu bar,
        // which is always on screen; the line stopped naming `?` for it on 2026-09-28.
        using var driving = Driving.Open(width: 40, height: 20);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(40);

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            shell.Lay(40);

            Assert.Contains(Shell.FocusPrefix, shell.Status, StringComparison.Ordinal);
            Assert.DoesNotContain($"{Shell.ActionsKey}:", shell.Status, StringComparison.Ordinal);
            Assert.NotEmpty(shell.BarTitles());
        }
    }

    [Fact]
    public void A_resize_keeps_the_same_regions_rather_than_building_new_ones()
    {
        // What lets a person's selection, a scroll position and an open transcript survive somebody
        // dragging a window edge. Rebuilding the regions would lose all three and look like a
        // refresh nobody asked for.
        using var driving = Driving.Open(width: 100, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var before = shell.Conversations;

        shell.Lay(40);
        shell.Lay(100);

        Assert.Same(before, shell.Conversations);
    }

    [Fact]
    public void A_long_transcript_line_is_wrapped_rather_than_cut()
    {
        // The rule 004 broke: a column that does not fit is dropped with its heading, never cut
        // mid-word, and prose is wrapped rather than either. The words most likely to be longer
        // than a narrow region are identifiers and paths, which are what somebody opened the
        // transcript to check.
        var wrapped = TranscriptRegion.Wrap("orders@test asked billing@test about the totals", 20);

        Assert.All(wrapped, line => Assert.True(line.Length <= 20, $"'{line}' is longer than the region"));
        Assert.Equal(
            "orders@test asked billing@test about the totals",
            string.Join(" ", wrapped));
    }

    [Fact]
    public void A_word_longer_than_the_region_is_broken_rather_than_lost()
    {
        var wrapped = TranscriptRegion.Wrap(new string('a', 25), 10);

        Assert.Equal(3, wrapped.Count);
        Assert.Equal(new string('a', 25), string.Concat(wrapped));
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
