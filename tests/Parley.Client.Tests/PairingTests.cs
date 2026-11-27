using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Who owes the next move, joined from the two reads a surface already makes.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets one input line decide what typing means, so every state it can report is probed rather than
/// reasoned about — including the two that produce nothing, which are the ones a derivation gets wrong quietly.
/// </para>
/// <para>
/// The join exists because <see cref="ExchangeSnapshot"/> carries no parties. Working them out from the messages
/// instead would have put the broker's turn logic in a client; the broker publishes it as the wait-for graph, so
/// the graph is what is read.
/// </para>
/// </remarks>
[Unit]
public sealed class PairingTests
{
    private static readonly AgentName Me = AgentName.Create("arvind@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static Message Message(long exchange, AgentName from, AgentName to) =>
        new(exchange, exchange, MessageType.Request, from, to, "m", """{"ask":"x"}""", "room", T0, T0);

    private static ConversationExport Export(
        IReadOnlyList<Message> messages, params (long Id, ExchangeState State)[] exchanges) =>
        new(messages, [.. exchanges.Select(e => new ExchangeSnapshot(e.Id, e.State, T0, null))]);

    [Fact]
    public void A_peer_that_asked_and_is_waiting_on_this_person_is_owed_by_this_person()
    {
        // The state whose act cannot be derived and must be offered. It has to be reported unambiguously or the
        // line has nothing to offer from.
        var export = Export([Message(1, Billing, Me)], (1, ExchangeState.Open));
        var edges = new[] { new WaitingEdge(Billing, Me, 1) };

        var pair = Assert.Single(Pairing.From(export, edges, Me));

        Assert.Equal(Billing, pair.Peer);
        Assert.Equal(Turn.Mine, pair.Turn);
        Assert.Equal(1, pair.OpenExchange);
    }

    [Fact]
    public void A_peer_this_person_asked_and_is_waiting_on_is_owed_by_the_peer()
    {
        var export = Export([Message(2, Me, Orders)], (2, ExchangeState.Open));
        var edges = new[] { new WaitingEdge(Me, Orders, 2) };

        var pair = Assert.Single(Pairing.From(export, edges, Me));

        Assert.Equal(Turn.Theirs, pair.Turn);
        Assert.Equal(2, pair.OpenExchange);
    }

    [Fact]
    public void A_participant_with_nothing_open_is_owed_by_nobody()
    {
        var export = Export([Message(3, Me, Orders)], (3, ExchangeState.Closed));

        var pair = Assert.Single(Pairing.From(export, [], Me));

        Assert.Equal(Turn.Nobody, pair.Turn);
        Assert.Null(pair.OpenExchange);
    }

    [Fact]
    public void An_edge_for_a_closed_exchange_is_ignored()
    {
        // The graph is the deployment's and is read at a different instant from the export. An edge naming an
        // exchange this conversation has since closed must not make work look owed.
        var export = Export([Message(4, Billing, Me)], (4, ExchangeState.Closed));
        var edges = new[] { new WaitingEdge(Billing, Me, 4) };

        Assert.Equal(Turn.Nobody, Assert.Single(Pairing.From(export, edges, Me)).Turn);
    }

    [Fact]
    public void An_edge_from_another_conversation_is_ignored()
    {
        // `DescribeWaiting` reports every pair in the deployment. Unfiltered, somebody else's work would appear
        // to be owed here — which would offer an act on an exchange this conversation does not hold.
        var export = Export([Message(5, Billing, Me)], (5, ExchangeState.Open));
        var edges = new[] { new WaitingEdge(Billing, Me, 99) };

        Assert.Equal(Turn.Nobody, Assert.Single(Pairing.From(export, edges, Me)).Turn);
    }

    [Fact]
    public void An_exchange_between_two_other_participants_yields_no_state_for_this_person()
    {
        // §5.0 gives nobody a way to join somebody else's exchange, so typing to either of them opens a new one.
        // Reported as nothing open rather than as an exchange to write into.
        var export = Export(
            [Message(6, Billing, Orders), Message(7, Me, Billing)],
            (6, ExchangeState.Open),
            (7, ExchangeState.Closed));

        var edges = new[] { new WaitingEdge(Billing, Orders, 6) };

        var pairs = Pairing.From(export, edges, Me);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(Turn.Nobody, p.Turn));
    }

    [Fact]
    public void What_this_person_owes_wins_over_what_they_are_waiting_on()
    {
        // Two open exchanges with one peer, in both directions. The one this person owes is the one whose act
        // has to be offered, so it must be the state reported.
        var export = Export(
            [Message(8, Billing, Me), Message(9, Me, Billing)],
            (8, ExchangeState.Open),
            (9, ExchangeState.Open));

        var edges = new[] { new WaitingEdge(Billing, Me, 8), new WaitingEdge(Me, Billing, 9) };

        var pair = Assert.Single(Pairing.From(export, edges, Me));

        Assert.Equal(Turn.Mine, pair.Turn);
        Assert.Equal(8, pair.OpenExchange);
    }

    [Fact]
    public void This_person_is_never_their_own_peer()
    {
        var export = Export([Message(10, Me, Billing)], (10, ExchangeState.Open));

        Assert.DoesNotContain(Pairing.From(export, [], Me), p => p.Peer == Me);
    }

    [Fact]
    public void Participants_are_ordered_so_a_screen_does_not_reorder_under_a_reader()
    {
        var export = Export(
            [Message(11, Orders, Me), Message(12, Billing, Me)],
            (11, ExchangeState.Closed),
            (12, ExchangeState.Closed));

        Assert.Equal([Billing, Orders], Pairing.From(export, [], Me).Select(p => p.Peer));
    }
}
