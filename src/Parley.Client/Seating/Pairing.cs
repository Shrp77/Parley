using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>Whose move it is between a seated person and one other participant.</summary>
public enum Turn
{
    /// <summary>Nothing is open between them.</summary>
    Nobody = 0,

    /// <summary>The peer is waiting on this person.</summary>
    Mine = 1,

    /// <summary>This person is waiting on the peer.</summary>
    Theirs = 2
}

/// <summary>
/// What stands between a seated person and one other participant of a conversation.
/// </summary>
/// <param name="Peer">The other participant.</param>
/// <param name="OpenExchange">The open exchange between them, or <see langword="null"/> when there is none.</param>
/// <param name="Turn">Whose move it is.</param>
/// <remarks>
/// A display fact for the room column only. It never decides what a send is: a person's line to an agent is
/// always a new Request, and a reply to a question the person chose to answer is a Clarify they ask for.
/// </remarks>
public sealed record PairState(AgentName Peer, long? OpenExchange, Turn Turn);

/// <summary>
/// The pair state for every participant of a conversation, from two reads the surface already makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Joined rather than reconstructed.</b> <see cref="ExchangeSnapshot"/> carries no parties, and the broker
/// already publishes whose move it is as the wait-for graph (§10.2), so the graph is read and filtered to this
/// conversation's exchanges rather than working turns out from messages in a client.
/// </para>
/// <para>
/// An exchange open between two <em>other</em> participants yields no pair state for this person, which is
/// correct rather than a gap: §5.0 gives nobody a way to join somebody else's exchange.
/// </para>
/// </remarks>
public static class Pairing
{
    /// <summary>Reads the pair state for every participant other than this person.</summary>
    /// <param name="export">The conversation, with its messages and the state of every exchange in it.</param>
    /// <param name="waiting">The wait-for graph, as the broker reports it across the deployment.</param>
    /// <param name="me">The seated person.</param>
    /// <returns>One entry per other participant, ordered by name so a screen does not reorder under a reader.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<PairState> From(
        ConversationExport export,
        IReadOnlyList<WaitingEdge> waiting,
        AgentName me)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(waiting);

        var open = export.Exchanges
            .Where(e => e.State == ExchangeState.Open)
            .Select(e => e.ExchangeId)
            .ToHashSet();

        // Filtered to this conversation, because the graph is the whole deployment's. An edge for an exchange
        // this conversation does not hold would make somebody else's work look like it was owed here.
        var edges = waiting.Where(edge => open.Contains(edge.ExchangeId)).ToList();

        var states = new List<PairState>();

        foreach (var peer in Participants(export).Where(p => p != me).OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            // Mine first: an exchange the peer waits on this person for wins over one they are merely waiting on.
            var owed = edges.FirstOrDefault(e => e.WaitedOn == me && e.Waiter == peer);

            if (owed is not null)
            {
                states.Add(new PairState(peer, owed.ExchangeId, Turn.Mine));

                continue;
            }

            var waited = edges.FirstOrDefault(e => e.Waiter == me && e.WaitedOn == peer);

            states.Add(waited is null
                ? new PairState(peer, null, Turn.Nobody)
                : new PairState(peer, waited.ExchangeId, Turn.Theirs));
        }

        return states;
    }

    /// <summary>Everyone party to a message in the conversation.</summary>
    /// <param name="export">The conversation.</param>
    /// <returns>The participants, without duplicates.</returns>
    /// <remarks>
    /// Derived from the messages because §5.0 says membership is derived: a participant is anyone party to an
    /// exchange in the conversation. There is no list to read, which is also why nothing can be added to one
    /// without being sent something.
    /// </remarks>
    public static IReadOnlyList<AgentName> Participants(ConversationExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var seen = new HashSet<AgentName>();

        foreach (var message in export.Messages)
        {
            seen.Add(message.From);
            seen.Add(message.To);
        }

        return [.. seen];
    }
}
