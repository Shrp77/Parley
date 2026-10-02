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
    }

    [Fact]
    public void Waiting_on_them_opens_a_new_exchange_rather_than_clarifying_theirs()
    {
        // **This asserted a Clarify, and the broker refuses one.** 017's own spec said that where this person
        // asked and is waiting, typing is a Clarify "because nobody may Result their own request" — which is
        // true about the Result and wrong about the remedy. SPEC.md §5.2 gives the Clarify to the *receiver*
        // asking the requester for more, so a requester clarifying their own open request is refused with
        // "the other side holds the next move on that exchange". The owner met it as a message that reached
        // nobody. There is nothing to add to an exchange whose next move is not yours, so typing opens one.
        var plan = Assert.Single(Over(new PairState(Orders, 7, Turn.Theirs)).Plan());

        Assert.Equal(Act.Ask, plan.Act);
        Assert.Null(plan.Exchange);
    }

    [Fact]
    public void And_a_move_owed_on_ones_own_request_is_a_clarify_rather_than_a_result()
    {
        // The other half of the same correction: whose move it is does not say which act. Where this person
        // asked and the peer clarified, what is owed is the answer to that clarification — and §5.3 keeps the
        // Result for the receiver, which is why a Result here came back as "you sent that request, so its
        // receiver sends the Result".
        var plan = Assert.Single(Over(new PairState(Orders, 7, Turn.Mine, Asked: true)).Plan());

        Assert.Equal(Act.Clarify, plan.Act);
        Assert.Equal(7, plan.Exchange);
    }

    [Fact]
    public void Holding_their_request_answers_it()
    {
        // **Always, since 2026-10-02.** This was a default with an "ask back instead" on F2; the owner removed the
        // choice, because a conversation never really stops and a reply that is a question is still a reply.
        var draft = Over(new PairState(Billing, 3, Turn.Mine));

        var plan = Assert.Single(draft.Plan());

        Assert.Equal(Act.Answer, plan.Act);
        Assert.Equal(3, plan.Exchange);
    }

    [Fact]
    public void One_line_to_three_recipients_is_three_different_acts()
    {
        // The case the preview exists for. Each is correct for its own pair, and a person who could not see all
        // three would be pressing Enter on something they had no way to predict.
        var draft = Over(
            new PairState(Billing, 3, Turn.Mine),
            new PairState(Orders, 7, Turn.Mine, Asked: true),
            new PairState(Inventory, null, Turn.Nobody));

        draft.Text = "and refunds?";

        var plan = draft.Plan();

        // Billing asked and this person holds it, so it closes. Orders was asked *by* this person and has
        // clarified, so what is owed is the answer to that clarification. Inventory has nothing open.
        Assert.Equal(Act.Answer, plan.First(a => a.Peer == Billing).Act);
        Assert.Equal(Act.Clarify, plan.First(a => a.Peer == Orders).Act);
        Assert.Equal(Act.Ask, plan.First(a => a.Peer == Inventory).Act);
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
    }
}
