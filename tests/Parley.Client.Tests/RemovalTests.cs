using Parley.Client.Roadmaps;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// What an edit that removes items would leave in flight (SPEC.md §5.7), the derivation both surfaces warn from.
/// </summary>
public sealed class RemovalTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private static RoadmapItem Item(int number, int open, int? removedRevision = null) =>
        new(
            number,
            number,
            number,
            $"Item {number}",
            Billing,
            [],
            $"rm1-{number}",
            removedRevision,
            removedRevision is null
                ? new ItemFacts(open > 0, open, false, 0, 0, 0, null, null, false, false, [.. Enumerable.Range(1, open).Select(i => (long)i)])
                : null);

    private static Roadmap Roadmap(params RoadmapItem[] items) =>
        new(1, 1, "Billing export", RoadmapOrigin.Proposed, null, 3, items);

    private static RoadmapContent Keeping(params int[] numbers) =>
        new("Billing export", [.. numbers.Select(n => new RoadmapItemDraft(n, $"Item {n}", Billing, []))]);

    [Fact]
    public void Only_a_removed_item_with_open_work_is_named()
    {
        // Must fail against warning for every removal, and against warning for open work on an item that is kept.
        var roadmap = Roadmap(Item(1, open: 1), Item(2, open: 0), Item(3, open: 2));

        var found = Removal.StillOpen(roadmap, Keeping(1));

        Assert.Equal([3], found.Select(i => i.Number));
    }

    [Fact]
    public void An_item_already_removed_is_not_named_again()
    {
        var roadmap = Roadmap(Item(1, open: 0), Item(2, open: 0, removedRevision: 2));

        Assert.Empty(Removal.StillOpen(roadmap, Keeping(1)));
    }

    [Fact]
    public void Nothing_to_say_is_null_and_one_item_is_said_by_number_words_and_conversation()
    {
        Assert.Null(Removal.Describe([]));

        var said = Removal.Describe([Item(3, open: 1)]);

        Assert.StartsWith("This removes an item whose work is still open: item 3, \"Item 3\", in rm1-3.", said, StringComparison.Ordinal);
        Assert.Contains("not told", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_item_is_quoted_short_so_every_item_and_what_removing_means_stay_in_view()
    {
        // An agent's item words have no bound, and a dialog showing a long description loses its tail. Must fail against
        // quoting each item whole, which let one item push the next, and the consequence, off the person's screen.
        var longOne = Item(1, open: 1) with { Text = new string('w', 5_800) };
        var said = Removal.Describe([longOne, Item(2, open: 1)])!;

        Assert.True(said.Length < 400, $"the description is {said.Length} characters");
        Assert.Contains("item 2, \"Item 2\", in rm1-2", said, StringComparison.Ordinal);
        Assert.Contains("not told", said, StringComparison.Ordinal);
        Assert.Contains(new string('w', AgentWords.ShortLength - 1) + "…", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3,3,1,1", true)]
    [InlineData(" 3 , 1 ", true)]
    [InlineData("3", false)]
    [InlineData("-1,-3", false)]
    [InlineData("1,99999999999,3", true)]
    [InlineData("99999999999", false)]
    [InlineData("+1,+3", false)]
    [InlineData("1,3,", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("0,1", false)]
    public void Going_ahead_covers_only_the_plain_numbers_of_the_items_described(string? agreed, bool covered)
    {
        // A form's hidden field is the person's agreement, so anything in it that is not an item number agrees to nothing.
        var open = Removal.StillOpen(Roadmap(Item(1, open: 1), Item(2, open: 0), Item(3, open: 1)), Keeping(2));

        Assert.Equal(covered, Removal.Covers(open, agreed));
        Assert.True(Removal.Covers([], "junk,-1,,"));
        Assert.True(Removal.Covers(open, Removal.Numbers(open)));
    }
}
