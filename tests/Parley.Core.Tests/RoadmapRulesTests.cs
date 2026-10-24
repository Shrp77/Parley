using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The names an item's conversation gets, and what makes a roadmap's content whole (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// The probes here are the malicious cases rather than the pleasant ones: a name that is not legal, a
/// dependency that points forward, text that breaks a line. A rule is worth what its refusal is worth.
/// </remarks>
public sealed class RoadmapRulesTests
{
    private static RoadmapItemDraft Item(string text, params int[] needs) =>
        new(null, text, null, needs);

    private static RoadmapContent Content(params RoadmapItemDraft[] items) => new("Billing export", items);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(12, 140)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void An_item_conversation_name_is_always_a_legal_conversation_identifier(int roadmap, int item)
    {
        var name = ItemConversation.Name(roadmap, item);

        // The same rule the conversations table holds: 1 to 64 characters of letters, digits, dot, underscore
        // and hyphen, and no run of dots. A name this class made must never be one storage would refuse.
        Assert.InRange(name.Length, 1, 64);
        Assert.Matches("^[a-zA-Z0-9._-]+$", name);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    [Fact]
    public void No_two_items_share_a_name()
    {
        // rm1-11 and rm11-1 are the pair a separator-less scheme would collide on.
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var roadmap = 1; roadmap <= 40; roadmap++)
        {
            for (var item = 1; item <= 40; item++)
            {
                Assert.True(names.Add(ItemConversation.Name(roadmap, item)), $"rm{roadmap}-{item} repeats");
            }
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void A_name_is_never_made_for_a_number_that_is_not_one_or_more(int roadmap, int item) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemConversation.Name(roadmap, item));

    [Fact]
    public void A_whole_roadmap_has_no_problem()
    {
        Assert.Null(RoadmapRules.Problem(Content(Item("Add CSV export"), Item("Call it from the report", 1))));
    }

    [Fact]
    public void An_empty_roadmap_is_refused()
    {
        Assert.NotNull(RoadmapRules.Problem(Content()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("a\0b")]
    public void An_item_that_is_blank_or_would_break_a_line_is_refused(string text)
    {
        var problem = RoadmapRules.Problem(Content(Item(text)));

        Assert.NotNull(problem);
        Assert.Contains("Item 1", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_that_breaks_a_line_is_refused()
    {
        Assert.NotNull(RoadmapRules.Problem(new RoadmapContent("two\nlines", [Item("Do it")])));
    }

    [Fact]
    public void There_is_no_length_bound()
    {
        // Constitution 6.0.0 and SPEC.md section 16: nothing here is read by an agent, and what is sent
        // travels in a Request that keeps its own cap. A bound reappearing in this class would be the
        // quiet reversal of a decision taken with the risk written down.
        Assert.Null(RoadmapRules.Problem(new RoadmapContent(new string('t', 50_000), [Item(new string('x', 500_000))])));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_item_cannot_need_itself_a_later_item_or_nothing(int needs)
    {
        // Position 2 may need only position 1. A dependency that points forward is a cycle waiting to
        // happen, so the shape makes one impossible rather than checking for one.
        var content = Content(Item("First"), Item("Second", needs), Item("Third"));

        var problem = RoadmapRules.Problem(content);

        if (needs == 1)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.NotNull(problem);
            Assert.Contains("Item 2", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_item_names_each_need_once()
    {
        Assert.NotNull(RoadmapRules.Problem(Content(Item("First"), Item("Second", 1, 1))));
    }

    [Fact]
    public void A_reordered_list_that_leaves_an_item_needing_a_later_one_is_refused()
    {
        // The case a trigger on insert cannot see: nothing was inserted, a person moved things. Item three
        // needed item one; after the move the list says item one now sits after it.
        var reordered = Content(Item("Document", 2), Item("Add export"));

        Assert.NotNull(RoadmapRules.Problem(reordered));
    }

    [Fact]
    public void An_existing_item_cannot_appear_twice()
    {
        var content = new RoadmapContent("Plan", [
            new RoadmapItemDraft(4, "One", null, []),
            new RoadmapItemDraft(4, "Two", null, []),
        ]);

        Assert.NotNull(RoadmapRules.Problem(content));
    }
}
