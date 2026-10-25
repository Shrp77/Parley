using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a proposal's title, however long, leaves who proposed it and what it proposes on screen beside Confirm.
/// </summary>
/// <remarks>
/// <b>Found by review, round 17.</b> Nothing bounds a title, and the heading was as tall as its title, so about 3,000
/// characters pushed the proposer and every item off the dialog while Confirm stayed on it.
/// </remarks>
public sealed class ALongProposalTitleHidesNothingTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public void A_long_title_does_not_hide_the_proposer_or_the_items(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);

        // About 3,000 characters: what one agent can put in a title, since nothing bounds it.
        var title = string.Concat(Enumerable.Repeat("Quarterly billing export ", 120)).Trim();

        var content = new RoadmapContent(
            title,
            [
                new RoadmapItemDraft(null, "Delete the export bucket", Billing, []),
                new RoadmapItemDraft(null, "Rotate the billing keys", Billing, [1]),
            ]);

        var proposal = new RoadmapProposal(1, Orders, null, null, content, title.Length, ProposalState.Waiting, T0, null);

        Prompting.Open(driving.App, new ProposalView(proposal));

        // Whatever scrolling the view offers, the person tries.
        for (var press = 0; press < 200; press++)
        {
            driving.Press(Key.CursorDown);
        }

        driving.App.LayoutAndDraw(true);

        var screen = driving.Text();

        Assert.Contains(ProposalView.ConfirmLabel, screen, StringComparison.Ordinal);
        Assert.Contains("Proposed by orders@test", screen, StringComparison.Ordinal);
        Assert.Contains("1. Delete the export bucket", screen, StringComparison.Ordinal);
    }
}
