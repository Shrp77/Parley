using System.Drawing;
using Parley.Core;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>That a dialog's buttons are all on it at the sizes a terminal has, wrapping rather than running off the edge.</summary>
/// <remarks>
/// Laid out in memory, with no screen: the nightly run found "Leave (Esc)" off a proposed roadmap at eighty columns once
/// every label named its key, and only a driven test could see it. This sees it in a unit run.
/// </remarks>
[Unit]
public sealed class ButtonRowsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    [Theory]
    [InlineData(140, 40)]
    [InlineData(100, 30)]
    [InlineData(80, 24)]
    [InlineData(50, 24)]
    public void Every_button_on_a_proposed_roadmap_is_inside_it_and_clear_of_the_rest(int width, int height)
    {
        using var view = Proposal();

        view.Layout(new Size(width, height));

        var buttons = view.SubViews.OfType<Button>().ToList();
        var inside = new Rectangle(Point.Empty, view.Viewport.Size);

        Assert.Equal(4, buttons.Count);
        Assert.All(buttons, button => Assert.True(inside.Contains(button.Frame), $"{button.Title} at {button.Frame} is outside {inside}"));

        foreach (var (one, other) in buttons.SelectMany((one, at) => buttons.Skip(at + 1).Select(other => (one, other))))
        {
            Assert.False(one.Frame.IntersectsWith(other.Frame), $"{one.Title} at {one.Frame} overlaps {other.Title} at {other.Frame}");
        }

        // The hint ends above the first row of buttons, and the roadmap above the hint.
        var hint = view.SubViews.Single(sub => sub.Text == ProposalView.Hint);
        var top = buttons.Min(button => button.Frame.Top);

        Assert.True(hint.Frame.Bottom <= top, $"the hint at {hint.Frame} runs into the buttons at row {top}");
        // The roadmap and who it names, below the heading.
        var roadmap = view.SubViews.Where(sub => sub is not (Button or Plain) && sub.Frame.Top > 0).ToList();

        Assert.Equal(2, roadmap.Count);
        Assert.All(
            roadmap,
            text => Assert.True(text.Frame.Bottom < hint.Frame.Top, $"the roadmap at {text.Frame} runs into the hint at {hint.Frame}"));
    }

    [Fact]
    public void Buttons_that_fit_stay_in_one_row_and_those_that_do_not_wrap()
    {
        using var wide = Proposal();
        using var narrow = Proposal();

        wide.Layout(new Size(140, 40));
        narrow.Layout(new Size(80, 24));

        Assert.Single(wide.SubViews.OfType<Button>().Select(button => button.Frame.Top).Distinct());
        Assert.Equal(2, narrow.SubViews.OfType<Button>().Select(button => button.Frame.Top).Distinct().Count());
    }

    private static ProposalView Proposal()
    {
        var content = new RoadmapContent("Ship it", [new RoadmapItemDraft(null, "Write the notes", Orders, [])]);

        return new ProposalView(new RoadmapProposal(1, Orders, null, "Ship it", content, 10, ProposalState.Waiting, DateTimeOffset.UnixEpoch, null));
    }
}
