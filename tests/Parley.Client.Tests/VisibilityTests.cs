using Parley.Core;
using Parley.Client.Rendering;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The arithmetic that stops a chat transcript lying about who read what.
/// </summary>
/// <remarks>
/// <para>
/// The case is SPEC.md §5.0's own: <c>orders</c> asks <c>billing</c>; <c>orders</c> asks
/// <c>inventory</c> under the same identifier; then <c>billing</c> and <c>inventory</c> exchange
/// one. Three participants, one conversation, and still only pairwise traffic — no message ever
/// addresses more than one peer, and no agent learns what the others said.
/// </para>
/// <para>
/// Rendered as an unannotated thread that reads as a group chat, in which every member has seen
/// every message. The operator then reasons about a disagreement neither agent could have had,
/// and nothing on the page says otherwise. That is the defect these exist for.
/// </para>
/// </remarks>
[Unit]
public sealed class VisibilityTests
{
    private static AgentName Orders => AgentName.Create("orders@alpha");

    private static AgentName Billing => AgentName.Create("billing@alpha");

    private static AgentName Inventory => AgentName.Create("inventory@alpha");

    private static Message Between(AgentName from, AgentName to, long id) =>
        new(id, id, MessageType.Request, from, to, "some-model-9", "{}", "shared",
            DateTimeOffset.UnixEpoch.AddMinutes(id), null);

    /// <summary>The three-participant conversation of §5.0, in the order it happened.</summary>
    private static IReadOnlyList<Message> Conversation =>
    [
        Between(Orders, Billing, 1),
        Between(Orders, Inventory, 2),
        Between(Billing, Inventory, 3)
    ];

    [Fact]
    public void Everyone_party_to_an_exchange_is_a_participant()
    {
        var participants = Visibility.Participants(Conversation);

        Assert.Equal([Billing, Inventory, Orders], participants);
    }

    [Fact]
    public void A_message_was_seen_by_exactly_its_pair()
    {
        // A statement about the protocol rather than about this deployment: there is no case in
        // which a third agent saw a message.
        var seen = Visibility.SeenBy(Conversation[0]);

        Assert.Equal([Orders, Billing], seen);
    }

    [Fact]
    public void Inventory_did_not_see_what_billing_told_orders()
    {
        // The defect the annotation exists to prevent, stated as the case that produces it. An
        // operator reading an unannotated thread would conclude that inventory had this in front
        // of it.
        var participants = Visibility.Participants(Conversation);

        var absent = Visibility.NotSeenBy(Conversation[0], participants);

        Assert.Equal([Inventory], absent);
    }

    [Fact]
    public void The_annotation_is_words_rather_than_a_marker()
    {
        // Carried as text because a reader who cannot see a colour would otherwise lose exactly
        // the fact that stops the transcript misleading them.
        var participants = Visibility.Participants(Conversation);

        var said = Visibility.Describe(Conversation[0], participants);

        Assert.Contains("inventory@alpha", said, StringComparison.Ordinal);
        Assert.Contains("did not see", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_conversation_between_two_agents_annotates_nothing()
    {
        // Both participants saw everything, so there is nothing to say. A note on every line is a
        // note nobody reads, including on the line where it matters.
        IReadOnlyList<Message> pairOnly = [Between(Orders, Billing, 1), Between(Billing, Orders, 2)];

        var participants = Visibility.Participants(pairOnly);

        Assert.Equal(string.Empty, Visibility.Describe(pairOnly[0], participants));
    }

    [Fact]
    public void Every_message_in_the_case_excludes_exactly_one_agent()
    {
        // Three pairwise exchanges among three agents: each message leaves precisely one out.
        // Stated over the whole conversation rather than one message, because a derivation that
        // happened to be right about the first line is the way this goes wrong.
        var participants = Visibility.Participants(Conversation);

        foreach (var message in Conversation)
        {
            Assert.Single(Visibility.NotSeenBy(message, participants));
        }
    }

    [Fact]
    public void A_participant_who_only_ever_received_still_counts()
    {
        // Derived from both ends of every exchange. Taking participants from senders alone would
        // drop an agent that answered and never asked -- and it would drop it from the annotation
        // too, so the transcript would quietly stop mentioning that it had not seen things.
        IReadOnlyList<Message> oneWay = [Between(Orders, Billing, 1), Between(Orders, Inventory, 2)];

        Assert.Equal([Billing, Inventory, Orders], Visibility.Participants(oneWay));
    }
}
