using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>One question waiting on the person, and where it was said.</summary>
/// <param name="Exchange">The exchange it is on.</param>
/// <param name="AskedUtc">When the latest thing the agent said in it was written.</param>
/// <param name="At">That message, which is what a surface shows when the question is chosen; null when none is held.</param>
/// <param name="Asked">What it says, in the agent's words, whole; a surface cuts it to the room it has.</param>
public sealed record WaitingQuestion(long Exchange, DateTimeOffset AskedUtc, long? At, string Asked);

/// <summary>One agent and every question of its waiting on the person in a conversation.</summary>
/// <param name="Peer">The agent.</param>
/// <param name="Questions">Its questions, oldest first.</param>
public sealed record WaitingOn(AgentName Peer, IReadOnlyList<WaitingQuestion> Questions);

/// <summary>
/// What is waiting on the person in one conversation, one entry per agent (specs/025, SPEC.md §5.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every question, not one per agent.</b> <see cref="Pairing"/> keeps one exchange per peer, picked by the order the
/// wait-for graph lists its edges, and that pick is what filed the owner's reply against the wrong question. This keeps
/// all of them, so the person sees how many there are and can reach each one.
/// </para>
/// <para>
/// <b>One derivation for both surfaces</b>, so the terminal's room and the web page cannot disagree about who is waiting
/// on the person or how many questions each has (SC-005).
/// </para>
/// <para>
/// A question is an exchange in this conversation, still open, on which the person owes the next move — the agent's own
/// request, or the person's request that the agent asked back about. What it shows is the latest thing the agent said in
/// it, 0.30.1's rule, because that is what the person owes a reply to.
/// </para>
/// </remarks>
public static class WaitingOnYou
{
    /// <summary>Reads what is waiting on the person.</summary>
    /// <param name="export">The conversation.</param>
    /// <param name="waiting">The wait-for graph, as the broker reports it across the deployment.</param>
    /// <param name="me">The person.</param>
    /// <returns>One entry per agent with something waiting, ordered by name so a screen does not reorder under a reader.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<WaitingOn> From(
        ConversationExport export,
        IReadOnlyList<WaitingEdge> waiting,
        AgentName me)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(waiting);

        var open = export.Exchanges
            .Where(e => e.State == ExchangeState.Open)
            .ToDictionary(e => e.ExchangeId);

        return [.. waiting
            .Where(edge => edge.WaitedOn == me && edge.Waiter != me && open.ContainsKey(edge.ExchangeId))
            .DistinctBy(edge => edge.ExchangeId)
            .GroupBy(edge => edge.Waiter)
            .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
            .Select(group => new WaitingOn(
                group.Key,
                [.. group
                    .OrderBy(edge => edge.ExchangeId)
                    .Select(edge => Question(export, open[edge.ExchangeId], group.Key))]))];
    }

    /// <summary>One question, as the conversation holds it.</summary>
    /// <param name="export">The conversation.</param>
    /// <param name="exchange">Its exchange.</param>
    /// <param name="peer">The agent waiting on it.</param>
    /// <returns>The question.</returns>
    private static WaitingQuestion Question(ConversationExport export, ExchangeSnapshot exchange, AgentName peer)
    {
        var said = export.Messages.LastOrDefault(m => m.ExchangeId == exchange.ExchangeId && m.From == peer);

        return new WaitingQuestion(
            exchange.ExchangeId,
            said?.CreatedUtc ?? exchange.OpenedUtc,
            said?.Id,
            said is null ? string.Empty : PayloadShape.Spoken(said.PayloadJson));
    }
}
