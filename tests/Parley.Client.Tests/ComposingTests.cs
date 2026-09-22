using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// What one line of text turns into, per recipient.
/// </summary>
/// <remarks>
/// <para>
/// SC-002: every message is recorded as the act the preview named. That is a property of this derivation, so the
/// derivation is probed for every state it can be in, including the one it must refuse to settle by itself.
/// </para>
/// <para>
/// The reason the act is derived rather than chosen first: a person knows what they want to say, and which of
/// §5.1's Request, §5.2's Clarify and §5.3's Result that amounts to is bookkeeping about who owes whom.
/// </para>
/// </remarks>
public sealed class ComposingTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");

    private static Draft Over(params PairState[] pairs) => new(pairs);

    [Fact]
    public void Nothing_open_is_a_request()
    {
        var plan = Assert.Single(Over(new PairState(Orders, null, Turn.Nobody)).Plan());

        Assert.Equal(Act.Ask, plan.Act);
        Assert.Null(plan.Exchange);
        Assert.False(plan.Choosable);
    }

    [Fact]
    public void Waiting_on_them_is_a_clarify_and_never_a_result()
    {
        // Nobody may send a Result on their own request (§5.3), so this state has exactly one act and the
        // surface must not offer a choice that would be refused.
        var plan = Assert.Single(Over(new PairState(Orders, 7, Turn.Theirs)).Plan());

        Assert.Equal(Act.Clarify, plan.Act);
        Assert.Equal(7, plan.Exchange);
        Assert.False(plan.Choosable);
    }

    [Fact]
    public void Holding_their_request_answers_by_default_and_says_so()
    {
        var draft = Over(new PairState(Billing, 3, Turn.Mine));

        var plan = Assert.Single(draft.Plan());

        Assert.Equal(Act.Answer, plan.Act);
        Assert.Equal(3, plan.Exchange);

        // Choosable is what the preview reads to offer the other side. Without it the person is never told the
        // alternative exists, which is the whole of FR-008.
        Assert.True(plan.Choosable);
        Assert.True(draft.Answering(Billing));
    }

    [Fact]
    public void And_the_other_side_of_that_one_choice_can_be_taken()
    {
        var draft = Over(new PairState(Billing, 3, Turn.Mine));

        draft.Flip(Billing);

        Assert.Equal(Act.AskBack, Assert.Single(draft.Plan()).Act);
        Assert.False(draft.Answering(Billing));

        // And back, because a person who reconsiders must not have to clear the line to do it.
        draft.Flip(Billing);

        Assert.Equal(Act.Answer, Assert.Single(draft.Plan()).Act);
    }

    [Fact]
    public void Flipping_one_recipient_leaves_the_others_alone()
    {
        var draft = Over(
            new PairState(Billing, 3, Turn.Mine),
            new PairState(Inventory, 4, Turn.Mine));

        draft.Flip(Billing);

        var plan = draft.Plan();

        Assert.Equal(Act.AskBack, plan.First(a => a.Peer == Billing).Act);
        Assert.Equal(Act.Answer, plan.First(a => a.Peer == Inventory).Act);
    }

    [Fact]
    public void One_line_to_three_recipients_is_three_different_acts()
    {
        // The case the preview exists for. Each is correct for its own pair, and a person who could not see all
        // three would be pressing Enter on something they had no way to predict.
        var draft = Over(
            new PairState(Billing, 3, Turn.Mine),
            new PairState(Orders, 7, Turn.Theirs),
            new PairState(Inventory, null, Turn.Nobody));

        draft.Text = "and refunds?";

        var plan = draft.Plan();

        Assert.Equal(Act.Answer, plan.First(a => a.Peer == Billing).Act);
        Assert.Equal(Act.Clarify, plan.First(a => a.Peer == Orders).Act);
        Assert.Equal(Act.Ask, plan.First(a => a.Peer == Inventory).Act);
    }

    [Fact]
    public void The_cost_is_counted_in_exchanges_because_that_is_what_a_plural_ask_is()
    {
        var draft = Over(
            new PairState(Billing, 3, Turn.Mine),
            new PairState(Orders, 7, Turn.Theirs),
            new PairState(Inventory, null, Turn.Nobody));

        var cost = draft.Costs();

        Assert.Equal(1, cost.Opens);
        Assert.Equal(1, cost.Closes);
        Assert.Equal(1, cost.HandsBack);
    }

    [Fact]
    public void Asking_back_moves_a_close_into_a_handed_back_turn()
    {
        var draft = Over(new PairState(Billing, 3, Turn.Mine));

        Assert.Equal(1, draft.Costs().Closes);

        draft.Flip(Billing);

        Assert.Equal(0, draft.Costs().Closes);
        Assert.Equal(1, draft.Costs().HandsBack);
    }

    [Fact]
    public void Every_participant_is_a_recipient_until_it_is_narrowed()
    {
        var draft = Over(
            new PairState(Billing, null, Turn.Nobody),
            new PairState(Orders, null, Turn.Nobody));

        Assert.Equal(2, draft.Recipients.Count);

        draft.AddressTo([Orders]);

        Assert.Equal([Orders], draft.Recipients);
        Assert.Equal(1, draft.Costs().Opens);
    }

    [Fact]
    public void Addressing_a_participant_twice_sends_once()
    {
        // The picker can resolve the same agent twice, and §5.5 makes a recipient an exchange: twice would be
        // two exchanges for one intention, which is an obligation nobody asked for.
        var draft = Over(new PairState(Orders, null, Turn.Nobody));

        draft.AddressTo([Orders, Orders]);

        Assert.Equal([Orders], draft.Recipients);
    }

    [Fact]
    public void Addressing_somebody_with_no_pair_state_opens_a_request()
    {
        // `/add` widens to an agent that is not a participant, so no pair state exists for them. That is
        // nothing-open by definition, and it must not throw or silently drop them.
        var draft = Over(new PairState(Orders, null, Turn.Nobody));

        draft.AddressTo([Orders, Inventory]);

        Assert.Equal(Act.Ask, draft.Plan().First(a => a.Peer == Inventory).Act);
        Assert.Equal(2, draft.Costs().Opens);
    }

    [Fact]
    public void The_text_is_kept_exactly_as_typed()
    {
        // It is what reaches a payload, and a surface that trimmed or reflowed it would be editing somebody's
        // words on the way to somebody else's context.
        var draft = Over(new PairState(Orders, null, Turn.Nobody));

        draft.Text = "  /etc/hosts — is it readable?\n\nsecond paragraph  ";

        Assert.Equal("  /etc/hosts — is it readable?\n\nsecond paragraph  ", draft.Text);
    }

    [Fact]
    public void Every_participant_is_addressed_until_somebody_narrows_it()
    {
        var draft = Over(
            new PairState(Billing, null, Turn.Nobody),
            new PairState(Orders, 7, Turn.Mine));

        Assert.Equal([Billing, Orders], draft.Recipients);
        Assert.False(draft.Narrowed);
    }

    [Fact]
    public void The_first_at_sign_replaces_and_the_rest_add()
    {
        // The owner's own example: `@billing, @inventory; I want this` targets those two, so the second sigil
        // cannot be a second replacement. Nothing means "and everyone else as well", because addressing
        // somebody in particular is the act of not addressing the room.
        var draft = Over(
            new PairState(Billing, null, Turn.Nobody),
            new PairState(Orders, 7, Turn.Mine),
            new PairState(Inventory, null, Turn.Nobody));

        draft.Narrow(Billing);

        Assert.Equal([Billing], draft.Recipients);
        Assert.True(draft.Narrowed);

        draft.Narrow(Inventory);

        Assert.Equal([Billing, Inventory], draft.Recipients);
    }

    [Fact]
    public void Narrowing_to_the_same_agent_twice_addresses_it_once()
    {
        var draft = Over(new PairState(Billing, null, Turn.Nobody));

        draft.Narrow(Billing);
        draft.Narrow(Billing);

        Assert.Equal([Billing], draft.Recipients);
    }

    [Fact]
    public void All_addresses_every_participant_again_and_never_the_project()
    {
        // Every participant, never every agent in the project: membership is derived (SPEC.md 5.0) and this
        // word is typed inside one conversation.
        var draft = Over(
            new PairState(Billing, null, Turn.Nobody),
            new PairState(Orders, 7, Turn.Mine));

        draft.Narrow(Orders);
        draft.AddressEveryone();

        Assert.Equal([Billing, Orders], draft.Recipients);
        Assert.False(draft.Narrowed);
    }

    [Fact]
    public void Adding_widens_without_narrowing_and_the_added_agent_gets_a_request()
    {
        // What `/add` does. An agent that is not a participant has no pair state, so it gets a Request, which
        // is how an agent joins a conversation at all.
        var draft = Over(new PairState(Billing, null, Turn.Nobody));

        draft.Widen(Inventory);

        Assert.Equal([Billing, Inventory], draft.Recipients);
        Assert.False(draft.Narrowed);

        var added = Assert.Single(draft.Plan(), addressed => addressed.Peer == Inventory);

        Assert.Equal(Act.Ask, added.Act);
        Assert.Null(added.Exchange);
    }

    [Fact]
    public void Narrowing_does_not_change_what_any_recipient_act_is()
    {
        // The act is the pair's own answer, so who is addressed and what each gets are two independent facts.
        var draft = Over(
            new PairState(Billing, null, Turn.Nobody),
            new PairState(Orders, 7, Turn.Mine));

        draft.Narrow(Orders);

        var plan = Assert.Single(draft.Plan());

        Assert.Equal(Act.Answer, plan.Act);
        Assert.Equal(7, plan.Exchange);
        Assert.Equal(new Cost(0, 1, 0), draft.Costs());
    }
}
