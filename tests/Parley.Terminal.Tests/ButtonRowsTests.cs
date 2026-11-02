using System.Drawing;
using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>That a dialog's buttons are all on it at the sizes a terminal has, wrapping rather than running off the edge.</summary>
/// <remarks>
/// Laid out in memory, with no screen: the nightly run found "Leave (Esc)" off a proposed roadmap at eighty columns once
/// every label named its key, and only a driven test could see it. A confirmed roadmap's "Close (Esc)" was further off
/// still. This sees both in a unit run.
/// </remarks>
[Unit]
public sealed class ButtonRowsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    [Theory]
    [InlineData(Proposed, 140, 40)]
    [InlineData(Proposed, 100, 30)]
    [InlineData(Proposed, 80, 24)]
    [InlineData(Proposed, 50, 24)]
    [InlineData(Confirmed, 140, 40)]
    [InlineData(Confirmed, 100, 30)]
    [InlineData(Confirmed, 80, 24)]
    [InlineData(Confirmed, 50, 24)]
    public void Every_button_on_a_roadmap_is_inside_it_and_clear_of_the_rest(string dialog, int width, int height)
    {
        var (view, hintText) = Build(dialog);

        using (view)
        {
            view.Layout(new Size(width, height));

            var buttons = view.SubViews.OfType<Button>().ToList();
            var inside = new Rectangle(Point.Empty, view.Viewport.Size);

            Assert.Equal(4, buttons.Count);
            Assert.All(buttons, button => Assert.True(inside.Contains(button.Frame), $"{button.Title} at {button.Frame} is outside {inside}"));

            foreach (var (one, other) in buttons.SelectMany((one, at) => buttons.Skip(at + 1).Select(other => (one, other))))
            {
                Assert.False(one.Frame.IntersectsWith(other.Frame), $"{one.Title} at {one.Frame} overlaps {other.Title} at {other.Frame}");
            }

            // The hint ends above the first row of buttons.
            var hint = view.SubViews.Single(sub => sub.Text == hintText);
            var top = buttons.Min(button => button.Frame.Top);

            Assert.True(hint.Frame.Bottom <= top, $"the hint at {hint.Frame} runs into the buttons at row {top}");

            // And what is read above it ends above the hint: the roadmap and who it names, or the items and the item.
            var read = view.SubViews.Where(sub => sub is not (Button or Plain) && sub.Frame.Top > 0).ToList();

            Assert.Equal(2, read.Count);
            Assert.All(read, text => Assert.True(text.Frame.Bottom < hint.Frame.Top, $"{text.GetType().Name} at {text.Frame} runs into the hint at {hint.Frame}"));
        }
    }

    [Theory]
    [InlineData(Proposed)]
    [InlineData(Confirmed)]
    public void Buttons_that_fit_stay_in_one_row_and_those_that_do_not_wrap(string dialog)
    {
        var (wide, _) = Build(dialog);
        var (narrow, _) = Build(dialog);

        using (wide)
        using (narrow)
        {
            wide.Layout(new Size(140, 40));
            narrow.Layout(new Size(80, 24));

            Assert.Single(wide.SubViews.OfType<Button>().Select(button => button.Frame.Top).Distinct());
            Assert.Equal(2, narrow.SubViews.OfType<Button>().Select(button => button.Frame.Top).Distinct().Count());
        }
    }

    private const string Proposed = "proposed";

    private const string Confirmed = "confirmed";

    /// <summary>One of the dialogs, and the hint it shows above its buttons.</summary>
    /// <param name="dialog">Which.</param>
    /// <returns>The dialog, for the caller to dispose of, and its hint.</returns>
    private static (View View, string Hint) Build(string dialog)
    {
        if (dialog == Confirmed)
        {
            return (new PlanView(new Roadmap(1, 1, "Ship it", default, null, 1, []), new RoadmapStanding([], NextKind.Complete, null, 0)), PlanView.Hint);
        }

        var content = new RoadmapContent("Ship it", [new RoadmapItemDraft(null, "Write the notes", Orders, [])]);

        return (
            new ProposalView(new RoadmapProposal(1, Orders, null, "Ship it", content, 10, ProposalState.Waiting, DateTimeOffset.UnixEpoch, null)),
            ProposalView.Hint);
    }
}
