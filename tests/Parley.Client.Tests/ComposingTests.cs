using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Who one line of text goes to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every line is a Request</b> (SPEC.md §5.6), because with several of an agent's questions open the surface
/// would file the person's reply against whichever one the broker listed first. What a line becomes is held over a
/// real socket by <c>LineIsARequestOverTheWireTests</c> in the terminal's suite, where the broker records it.
/// </para>
/// <para>
/// What is tested here is addressing: who a line goes to, narrowing and widening it.
/// </para>
/// </remarks>
public sealed class ComposingTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");

    private static Draft Over(params PairState[] pairs) => new(pairs);

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
    public void Addressing_somebody_with_no_pair_state_keeps_them()
    {
        // `/add` widens to an agent that is not a participant, so no pair state exists for them. It must not throw or
        // silently drop them.
        var draft = Over(new PairState(Orders, null, Turn.Nobody));

        draft.AddressTo([Orders, Inventory]);

        Assert.Equal([Orders, Inventory], draft.Recipients);
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
        // For example: `@billing, @inventory; I want this` targets those two, so the second sigil
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
    public void Adding_widens_without_narrowing()
    {
        // What `/add` does. An agent that is not a participant joins by being sent a Request, like everyone else.
        var draft = Over(new PairState(Billing, null, Turn.Nobody));

        draft.Widen(Inventory);

        Assert.Equal([Billing, Inventory], draft.Recipients);
        Assert.False(draft.Narrowed);
    }
}
