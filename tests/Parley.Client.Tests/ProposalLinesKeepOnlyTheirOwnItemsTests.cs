using Parley.Client.Roadmaps;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Which lines of a proposal are given a live item's number before the person confirms it as a revision (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// A numbered line keeps that item and its conversation, so a number is given only to a line that repeats the item's own
/// words, each item at most once. A looser match would let a proposal put different words on an item's conversation.
/// </remarks>
[Unit]
public sealed class ProposalLinesKeepOnlyTheirOwnItemsTests
{
    private static RoadmapItem Item(int number, string text, int? removedRevision = null, int? position = null) =>
        new(number, number, position ?? number, text, null, [], $"rm1-{number}", removedRevision, null);

    private static Roadmap Holding(params RoadmapItem[] items) =>
        new(1, 1, "Billing export", RoadmapOrigin.Proposed, "orders@here", 2, items);

    private static RoadmapContent Proposing(params string[] lines) =>
        new("Billing export", [.. lines.Select(line => new RoadmapItemDraft(null, line, null, []))]);

    private static int?[] Numbers(RoadmapContent content) => [.. content.Items.Select(item => item.Number)];

    [Fact]
    public void A_line_with_an_items_exact_words_is_given_its_number_and_the_rest_are_left()
    {
        var roadmap = Holding(Item(1, "Add CSV export"), Item(2, "Document the export"));

        var (content, kept) = ProposalText.KeepingUnchanged(
            Proposing("Document the export", "Publish the export schema", "Add CSV export"), roadmap);

        Assert.Equal([2, null, 1], Numbers(content));
        Assert.Equal(2, kept);
        Assert.Equal(["Document the export", "Publish the export schema", "Add CSV export"], content.Items.Select(item => item.Text));
        Assert.Equal("Billing export", content.Title);
    }

    [Theory]
    [InlineData("add csv export")]
    [InlineData("ADD CSV EXPORT")]
    [InlineData("Add Csv export")]
    public void A_line_that_differs_only_in_case_is_not_given_the_items_number(string line)
    {
        var (content, kept) = ProposalText.KeepingUnchanged(Proposing(line), Holding(Item(1, "Add CSV export")));

        Assert.Equal([null], Numbers(content));
        Assert.Equal(0, kept);
    }

    [Theory]
    [InlineData("Add  CSV export")]
    [InlineData("Add CSV\texport")]
    [InlineData("AddCSV export")]
    [InlineData("Add CSV export.")]
    public void A_line_that_differs_inside_its_words_is_not_given_the_items_number(string line)
    {
        var (content, kept) = ProposalText.KeepingUnchanged(Proposing(line), Holding(Item(1, "Add CSV export")));

        Assert.Equal([null], Numbers(content));
        Assert.Equal(0, kept);
    }

    [Theory]
    [InlineData("  Add CSV export", "Add CSV export")]
    [InlineData("Add CSV export \t", "Add CSV export")]
    [InlineData("\tAdd CSV export\r\n", "Add CSV export")]
    [InlineData("Add CSV export", " Add CSV export  ")]
    public void A_line_that_matches_once_the_space_around_it_is_trimmed_is_given_the_items_number(string line, string item)
    {
        var (content, kept) = ProposalText.KeepingUnchanged(Proposing(line), Holding(Item(1, item)));

        Assert.Equal([1], Numbers(content));
        Assert.Equal(1, kept);

        // The line's own words go on as written: only the number is added.
        Assert.Equal(line, content.Items[0].Text);
    }

    [Fact]
    public void Of_two_identical_lines_only_the_first_is_given_the_items_number()
    {
        var (content, kept) = ProposalText.KeepingUnchanged(
            Proposing("Add CSV export", "Add CSV export"), Holding(Item(1, "Add CSV export"), Item(2, "Document the export")));

        Assert.Equal([1, null], Numbers(content));
        Assert.Equal(1, kept);
    }

    [Fact]
    public void Two_items_with_the_same_words_are_each_taken_once_in_the_roadmaps_order()
    {
        // Held out of order, so the order is the items' positions and never the order the broker returned them in.
        var roadmap = Holding(Item(4, "Add CSV export", position: 2), Item(3, "Add CSV export", position: 1));

        var (content, kept) = ProposalText.KeepingUnchanged(
            Proposing("Add CSV export", "Add CSV export", "Add CSV export"), roadmap);

        Assert.Equal([3, 4, null], Numbers(content));
        Assert.Equal(2, kept);
    }

    [Fact]
    public void A_removed_items_number_is_never_given()
    {
        var roadmap = Holding(Item(1, "Add CSV export", removedRevision: 2), Item(2, "Document the export"));

        var (content, kept) = ProposalText.KeepingUnchanged(Proposing("Add CSV export", "Document the export"), roadmap);

        Assert.Equal([null, 2], Numbers(content));
        Assert.Equal(1, kept);
    }

    [Fact]
    public void A_line_that_already_carries_a_number_is_left_as_it_is_and_takes_no_item()
    {
        var roadmap = Holding(Item(1, "Add CSV export"), Item(2, "Document the export"));
        var proposed = new RoadmapContent(
            "Billing export",
            [
                new RoadmapItemDraft(2, "Add CSV export", null, []),
                new RoadmapItemDraft(null, "Add CSV export", null, []),
            ]);

        var (content, kept) = ProposalText.KeepingUnchanged(proposed, roadmap);

        // The numbered line keeps the number the person gave it, and item 1 is still there for the line that repeats it.
        Assert.Equal([2, 1], Numbers(content));
        Assert.Equal(1, kept);
        Assert.Same(proposed.Items[0], content.Items[0]);
    }

    [Fact]
    public void A_roadmap_with_no_live_item_numbers_nothing()
    {
        var (content, kept) = ProposalText.KeepingUnchanged(
            Proposing("Add CSV export"), Holding(Item(1, "Add CSV export", removedRevision: 2)));

        Assert.Equal([null], Numbers(content));
        Assert.Equal(0, kept);
    }
}
