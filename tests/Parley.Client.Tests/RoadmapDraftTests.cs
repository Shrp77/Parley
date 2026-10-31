using Parley.Client.Roadmaps;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Editing a roadmap before it is confirmed or changed (SPEC.md §5.7).
/// </summary>
[Unit]
public sealed class RoadmapDraftTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@here");

    private static RoadmapContent Proposal() => new("Billing export",
    [
        new RoadmapItemDraft(null, "Add CSV export", Billing, []),
        new RoadmapItemDraft(null, "Call it from the report", null, [1]),
        new RoadmapItemDraft(null, "Document the column", null, [2]),
    ]);

    [Fact]
    public void A_draft_written_straight_back_out_is_the_content_it_began_as_and_has_not_changed()
    {
        var draft = RoadmapDraft.From(Proposal());

        Assert.False(draft.ChangedFrom(Proposal()));
        Assert.Null(draft.Problem());
        Assert.Equal([1], draft.ToContent().Items[1].Needs);
    }

    [Fact]
    public void Rewording_directing_and_retitling_each_count_as_a_change()
    {
        var reworded = RoadmapDraft.From(Proposal());
        reworded.Reword(0, "Add a CSV export endpoint");

        var directed = RoadmapDraft.From(Proposal());
        directed.Direct(1, Billing);

        var retitled = RoadmapDraft.From(Proposal());
        retitled.Title = "Another name";

        Assert.True(reworded.ChangedFrom(Proposal()));
        Assert.True(directed.ChangedFrom(Proposal()));
        Assert.True(retitled.ChangedFrom(Proposal()));
    }

    [Fact]
    public void Removing_an_item_stops_whatever_needed_it_and_says_which()
    {
        var draft = RoadmapDraft.From(Proposal());

        var affected = draft.Remove(0);

        Assert.Equal([0], affected);
        Assert.Equal(2, draft.Count);
        Assert.Empty(draft.ToContent().Items[0].Needs);

        // The last item still needs what was item two, now at position one.
        Assert.Equal([1], draft.ToContent().Items[1].Needs);
        Assert.Null(draft.Problem());
    }

    [Fact]
    public void A_move_changes_the_order_and_nothing_else_and_a_dependency_it_breaks_is_named()
    {
        var draft = RoadmapDraft.From(Proposal());

        // Item two needed item one. Moved above it, the list says it needs something that comes later.
        Assert.Equal(0, draft.Move(1, -1));

        var problem = draft.Problem();

        Assert.NotNull(problem);
        Assert.Contains("Item 1", problem, StringComparison.Ordinal);
        Assert.Equal(3, draft.Count);
    }

    [Fact]
    public void A_move_past_either_end_is_not_a_move()
    {
        var draft = RoadmapDraft.From(Proposal());

        Assert.Equal(0, draft.Move(0, -1));
        Assert.Equal(2, draft.Move(2, 1));
    }

    [Fact]
    public void A_new_item_can_go_anywhere_and_has_no_number()
    {
        var draft = RoadmapDraft.From(Proposal());

        var at = draft.Add("Announce it", after: 0);

        Assert.Equal(1, at);
        Assert.Equal("Announce it", draft.ToContent().Items[1].Text);
        Assert.Null(draft.ToContent().Items[1].Number);
        Assert.Equal([1], draft.ToContent().Items[2].Needs);
    }

    [Fact]
    public void An_item_cannot_be_made_to_need_itself()
    {
        var draft = RoadmapDraft.From(Proposal());

        draft.Need(2, [2, 0]);

        Assert.Equal([0], draft.Needs(2));
    }

    [Fact]
    public void A_confirmed_roadmap_keeps_each_items_number_through_a_reorder_so_its_conversation_is_kept()
    {
        var roadmap = new Roadmap(
            9,
            1,
            "Billing export",
            RoadmapOrigin.Proposed,
            "orders@here",
            2,
            [
                new RoadmapItem(1, 1, 1, "First", null, [], "rm1-1", null, null),
                new RoadmapItem(2, 2, 2, "Second", null, [1], "rm1-2", null, null),
                new RoadmapItem(3, 3, 3, "Removed", null, [], "rm1-3", 2, null),
                new RoadmapItem(4, 4, 4, "Fourth", null, [2], "rm1-4", null, null),
            ]);

        var draft = RoadmapDraft.From(roadmap);

        Assert.Equal(3, draft.Count);

        draft.Move(2, -1);

        var items = draft.ToContent().Items;

        // The removed item is not carried, and the numbers travel with the items rather than with their places.
        Assert.Equal([1, 4, 2], items.Select(i => i.Number));
        Assert.NotNull(draft.Problem());
    }

    [Fact]
    public void Content_that_names_no_such_item_is_kept_out_rather_than_invented()
    {
        var content = new RoadmapContent("Plan", [new RoadmapItemDraft(null, "Only", null, [7])]);

        var draft = RoadmapDraft.From(content);

        Assert.Empty(draft.ToContent().Items[0].Needs);
    }

    [Fact]
    public void A_line_break_or_a_blank_is_reported_by_the_same_rules_the_broker_applies()
    {
        var draft = RoadmapDraft.From(Proposal());

        draft.Reword(1, "two\nlines");

        Assert.NotNull(draft.Problem());

        draft.Reword(1, "   ");

        Assert.NotNull(draft.Problem());
    }

    [Fact]
    public void An_index_that_is_not_an_item_is_refused()
    {
        var draft = RoadmapDraft.From(Proposal());

        Assert.Throws<ArgumentOutOfRangeException>(() => draft.Reword(9, "x"));
    }
}
