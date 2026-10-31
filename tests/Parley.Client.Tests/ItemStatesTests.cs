using Parley.Client.Roadmaps;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Where a roadmap item stands, derived from the facts the broker holds, and which item is next (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// One case per state and per acceptance scenario of the story that asks for them. Each case is a fact pattern the
/// broker can really return, and a state that could not be read is held to never reading as not started.
/// </para>
/// </remarks>
[Unit]
public sealed class ItemStatesTests
{
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private static ItemFacts Untouched() => new(false, 0, false, 0, 0, 0, null, null, false, false, []);

    private static ItemFacts Open(bool collected, params long[] exchanges) =>
        new(true, exchanges.Length, collected, 0, 0, 0, null, null, false, false, exchanges);

    private static ItemFacts Closed(int complete = 0, int blocked = 0, int closed = 0, string? blocker = null, string? by = null) =>
        new(true, 0, false, complete, blocked, closed, blocker, by, false, false, []);

    private static RoadmapItem Item(int number, ItemFacts? facts, params int[] needs) =>
        new(number, number, number, $"Item {number}", null, needs, $"rm1-{number}", null, facts);

    private static Roadmap Plan(params RoadmapItem[] items) =>
        new(1, 1, "Plan", RoadmapOrigin.Proposed, null, 1, items);

    private static ItemStanding Standing(ItemFacts? facts, params WaitingEdge[] edges) =>
        ItemStates.Of(Plan(Item(1, facts)), edges, Person).Items[0].Standing;

    [Fact]
    public void An_item_with_no_conversation_is_not_started()
    {
        Assert.Equal(ItemStanding.NotStarted, Standing(Untouched()));
    }

    [Fact]
    public void A_request_an_agent_has_collected_is_in_progress_and_names_the_agent()
    {
        var states = ItemStates.Of(Plan(Item(1, Open(collected: true, 7))), [new WaitingEdge(Person, Billing, 7)], Person);

        Assert.Equal(ItemStanding.InProgress, states.Items[0].Standing);
        Assert.Equal([Billing], states.Items[0].Holders);
    }

    [Fact]
    public void A_request_nobody_has_collected_is_waiting_on_the_agent()
    {
        Assert.Equal(
            ItemStanding.WaitingOnAgent,
            Standing(Open(collected: false, 7), new WaitingEdge(Person, Billing, 7)));
    }

    [Fact]
    public void An_agent_waiting_on_another_agent_is_waiting_on_an_agent_not_in_progress()
    {
        // The person asked billing (exchange 7), and billing asked orders in the same conversation (exchange 8).
        var state = ItemStates.Of(
            Plan(Item(1, Open(collected: true, 7, 8))),
            [new WaitingEdge(Person, Billing, 7), new WaitingEdge(Billing, Orders, 8)],
            Person).Items[0];

        Assert.Equal(ItemStanding.WaitingOnAgent, state.Standing);
        Assert.Contains(Orders, state.Holders);
    }

    [Fact]
    public void An_agent_asking_the_person_something_is_waiting_on_you_and_the_roadmap_counts_it()
    {
        var standing = ItemStates.Of(
            Plan(Item(1, Open(collected: true, 7)), Item(2, Untouched())),
            [new WaitingEdge(Billing, Person, 7)],
            Person);

        Assert.Equal(ItemStanding.WaitingOnPerson, standing.Items[0].Standing);
        Assert.Equal(1, standing.WaitingOnYou);
    }

    [Fact]
    public void An_edge_in_another_conversation_does_not_make_this_item_wait_on_the_person()
    {
        // Exchange 99 belongs to somebody else's work: the graph is the whole deployment's.
        Assert.Equal(
            ItemStanding.InProgress,
            Standing(Open(collected: true, 7), new WaitingEdge(Person, Billing, 7), new WaitingEdge(Orders, Person, 99)));
    }

    [Fact]
    public void A_blocked_result_reads_blocked_and_says_the_blocker_the_agent_gave()
    {
        var state = ItemStates.Of(
            Plan(Item(1, Closed(blocked: 1, blocker: "permission"))), [], Person).Items[0];

        Assert.Equal(ItemStanding.Blocked, state.Standing);
        Assert.Equal("permission", state.Blocker);
    }

    [Theory]
    [InlineData("person", 0, 0)]
    [InlineData("person", 0, 1)]
    [InlineData("consensus", 0, 0)]
    [InlineData("person", 1, 0)]
    [InlineData("consensus", 1, 0)]
    public void A_blocked_item_whose_conversation_was_closed_reads_closed_names_its_blocker_and_is_never_done(
        string closedBy, int complete, int closedWithoutAnAnswer)
    {
        // Found by review, round 26: an item blocked in a conversation nobody can ask in again read blocked for good. Then
        // round 27: reading it closed dropped the blocker, though one agent may close a conversation holding nothing open,
        // and with another part done it read done, and the item that needs it became next.
        var standing = ItemStates.Of(
            Plan(
                Item(1, Closed(complete: complete, blocked: 1, closed: closedWithoutAnAnswer, blocker: "permission", by: closedBy)),
                Item(2, Untouched(), 1)),
            [],
            Person);

        Assert.Equal(ItemStanding.Closed, standing.Items[0].Standing);
        Assert.Equal("permission", standing.Items[0].Blocker);
        Assert.Null(standing.NextNumber);
    }

    [Fact]
    public void Open_work_beside_a_blocked_part_reads_as_that_work_and_still_names_the_blocker()
    {
        // Found by review, round 28: an item sent to two agents read "in progress" with the other's block not shown.
        var facts = new ItemFacts(true, 1, true, 0, 1, 0, "permission", null, false, false, [8]);

        var state = ItemStates.Of(Plan(Item(1, facts)), [new WaitingEdge(Person, Billing, 8)], Person).Items[0];

        Assert.Equal(ItemStanding.InProgress, state.Standing);
        Assert.Equal("permission", state.Blocker);
    }

    [Fact]
    public void Every_exchange_ending_in_a_result_is_done()
    {
        Assert.Equal(ItemStanding.Done, Standing(Closed(complete: 2)));
    }

    [Fact]
    public void A_conversation_a_person_closed_without_a_result_is_closed_never_done_and_never_not_started()
    {
        var closed = Standing(Closed(closed: 1, by: "person"));

        Assert.Equal(ItemStanding.Closed, closed);
        Assert.NotEqual(ItemStanding.Done, closed);
        Assert.NotEqual(ItemStanding.NotStarted, closed);
    }

    [Fact]
    public void A_destroyed_conversation_is_closed_and_not_not_started()
    {
        var facts = new ItemFacts(false, 0, false, 0, 0, 0, null, null, true, false, []);

        Assert.Equal(ItemStanding.Closed, Standing(facts));
    }

    [Fact]
    public void Some_done_and_some_closed_is_closed_because_the_work_was_not_all_done()
    {
        Assert.Equal(ItemStanding.Closed, Standing(Closed(complete: 1, closed: 1)));
    }

    [Fact]
    public void An_item_whose_facts_were_not_returned_could_not_be_read_and_is_never_not_started()
    {
        var state = ItemStates.Of(Plan(Item(1, null)), [], Person).Items[0];

        Assert.Equal(ItemStanding.CouldNotBeRead, state.Standing);
    }

    [Fact]
    public void An_open_exchange_with_no_wait_for_graph_could_not_be_read()
    {
        // Working, waiting on somebody and waiting on the person all look the same from the facts alone.
        var state = ItemStates.Of(Plan(Item(1, Open(collected: true, 7))), waiting: null, Person).Items[0];

        Assert.Equal(ItemStanding.CouldNotBeRead, state.Standing);
    }

    [Fact]
    public void An_item_with_nothing_open_does_not_need_the_graph()
    {
        var standing = ItemStates.Of(Plan(Item(1, Untouched()), Item(2, Closed(complete: 1))), waiting: null, Person);

        Assert.Equal(ItemStanding.NotStarted, standing.Items[0].Standing);
        Assert.Equal(ItemStanding.Done, standing.Items[1].Standing);
    }

    // ---- next

    [Fact]
    public void The_next_item_is_the_first_not_started_one_whose_needs_are_done()
    {
        var standing = ItemStates.Of(
            Plan(Item(1, Closed(complete: 1)), Item(2, Untouched(), 1), Item(3, Untouched(), 2)), [], Person);

        Assert.Equal(NextKind.Item, standing.Next);
        Assert.Equal(2, standing.NextNumber);
        Assert.True(standing.Items[1].IsNext);
        Assert.False(standing.Items[2].IsNext);
    }

    [Fact]
    public void A_later_item_is_not_next_while_what_it_needs_is_not_done()
    {
        // Item 2 is in progress, so item 3 which needs it is not ready, though it is not started.
        var standing = ItemStates.Of(
            Plan(Item(1, Closed(complete: 1)), Item(2, Open(collected: true, 7), 1), Item(3, Untouched(), 2)),
            [new WaitingEdge(Person, Billing, 7)],
            Person);

        Assert.Equal(NextKind.NothingReady, standing.Next);
        Assert.Null(standing.NextNumber);
    }

    [Fact]
    public void An_item_with_no_needs_is_next_even_when_an_earlier_one_is_in_progress()
    {
        // Order alone does not hold an item back: only what it needs does.
        var standing = ItemStates.Of(
            Plan(Item(1, Open(collected: true, 7)), Item(2, Untouched())),
            [new WaitingEdge(Person, Billing, 7)],
            Person);

        Assert.Equal(2, standing.NextNumber);
    }

    [Fact]
    public void Every_item_done_is_complete_and_names_no_next_item()
    {
        var standing = ItemStates.Of(Plan(Item(1, Closed(complete: 1)), Item(2, Closed(complete: 1))), [], Person);

        Assert.Equal(NextKind.Complete, standing.Next);
        Assert.Null(standing.NextNumber);
    }

    [Fact]
    public void A_roadmap_with_nothing_live_says_there_are_no_items()
    {
        var removed = new RoadmapItem(1, 1, 1, "Gone", null, [], "rm1-1", 2, null);

        var standing = ItemStates.Of(Plan(removed), [], Person);

        Assert.Equal(NextKind.Empty, standing.Next);
        Assert.Empty(standing.Items);
    }

    [Fact]
    public void A_removed_item_is_not_an_item_of_the_standing()
    {
        var removed = new RoadmapItem(2, 2, 2, "Gone", null, [], "rm1-2", 2, null);

        var standing = ItemStates.Of(Plan(Item(1, Untouched()), removed), [], Person);

        Assert.Single(standing.Items);
    }

    // ---- the words

    [Fact]
    public void An_item_edited_since_it_began_is_said_to_be_and_one_never_edited_is_not()
    {
        // The broker's fact, taken from the item's own edit record. The person may have trimmed or reworded the item's
        // words in the box before sending, which is not the item being reworded.
        var edited = new ItemFacts(true, 0, false, 1, 0, 0, null, null, false, true, []);
        var untouched = new ItemFacts(true, 0, false, 1, 0, 0, null, null, false, false, []);

        Assert.True(ItemStates.Of(Plan(Item(1, edited)), [], Person).Items[0].TextChanged);
        Assert.False(ItemStates.Of(Plan(Item(1, untouched)), [], Person).Items[0].TextChanged);
    }

    [Theory]
    [InlineData(ItemStanding.NotStarted, "[ ]")]
    [InlineData(ItemStanding.InProgress, "[>]")]
    [InlineData(ItemStanding.WaitingOnAgent, "[~]")]
    [InlineData(ItemStanding.WaitingOnPerson, "[?]")]
    [InlineData(ItemStanding.Blocked, "[!]")]
    [InlineData(ItemStanding.Done, "[x]")]
    [InlineData(ItemStanding.Closed, "[-]")]
    [InlineData(ItemStanding.CouldNotBeRead, "[*]")]
    public void Every_standing_has_its_own_three_character_mark(ItemStanding standing, string mark)
    {
        Assert.Equal(mark, ItemStates.Mark(standing));
        Assert.Equal(8, Enum.GetValues<ItemStanding>().Select(ItemStates.Mark).Distinct().Count());
    }
}
