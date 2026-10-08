using Parley.Client.Roadmaps;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The words a roadmap and its items are told in.
/// </summary>
public sealed class ItemWordingTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@host");

    private static ItemState State(
        ItemStanding standing, IReadOnlyList<AgentName>? holders = null, string? blocker = null, bool changed = false) =>
        new(
            new RoadmapItem(1, 1, 1, "Add CSV export", null, [], "rm1-1", null, null),
            standing,
            holders ?? [],
            blocker,
            changed,
            false);

    [Fact]
    public void A_row_names_the_roadmap_how_many_items_it_has_and_how_many_have_started()
    {
        Assert.Equal(
            "#2 Billing export — 5 items, 1 started",
            ItemWording.Row(new RoadmapSummary(7, 2, "Billing export", 1, 5, 1)));
    }

    [Fact]
    public void One_item_is_not_pluralised()
    {
        Assert.Contains("1 item,", ItemWording.Row(new RoadmapSummary(1, 1, "Solo", 1, 1, 0)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ItemStanding.NotStarted, "not started")]
    [InlineData(ItemStanding.WaitingOnPerson, "waiting on you")]
    [InlineData(ItemStanding.Done, "done")]
    [InlineData(ItemStanding.Closed, "closed")]
    public void Each_standing_has_its_own_words(ItemStanding standing, string expected)
    {
        Assert.Equal(expected, ItemWording.Standing(State(standing)));
    }

    [Fact]
    public void An_item_that_could_not_be_read_is_never_said_to_be_not_started()
    {
        // SPEC.md section 5.7. Must fail against collapsing the two.
        var said = ItemWording.Standing(State(ItemStanding.CouldNotBeRead));

        Assert.Equal("could not be read", said);
        Assert.DoesNotContain("not started", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Who_holds_an_item_and_the_blocker_the_agent_gave_are_named()
    {
        Assert.Equal("waiting on billing", ItemWording.Standing(State(ItemStanding.WaitingOnAgent, [Billing])));
        Assert.Equal("in progress with billing", ItemWording.Standing(State(ItemStanding.InProgress, [Billing])));
        Assert.Equal("blocked: permission", ItemWording.Standing(State(ItemStanding.Blocked, blocker: "permission")));
    }

    [Fact]
    public void An_item_reworded_since_it_began_says_so()
    {
        Assert.EndsWith("(reworded since it began)", ItemWording.Standing(State(ItemStanding.InProgress, changed: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void What_comes_next_is_an_item_or_the_reason_there_is_none()
    {
        Assert.Equal("Next: item 3.", ItemWording.Next(new RoadmapStanding([], NextKind.Item, 3, 0)));
        Assert.Equal("Every item is done.", ItemWording.Next(new RoadmapStanding([], NextKind.Complete, null, 0)));
        Assert.Contains("waiting on something or blocked", ItemWording.Next(new RoadmapStanding([], NextKind.NothingReady, null, 0)), StringComparison.Ordinal);
    }
}
