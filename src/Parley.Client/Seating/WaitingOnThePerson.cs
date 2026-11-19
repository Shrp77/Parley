using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>What waits on the person's answer: the count every surface shows, and the edges every list starts from.</summary>
/// <remarks>
/// <para>
/// <b>One filter for every surface's count and list</b> (owner, 2026-11-06: the console's count said 4 and its Inbox listed
/// 10). The count is every open exchange an agent is waiting on the person for (SPEC.md §5.6), from the broker's wait-for
/// graph, never the mail not yet collected: it stays until each question is answered or settled, and it is the number of
/// questions the person would find.
/// </para>
/// <para>
/// One call a project and nothing collected, so it is safe to ask while drawing a frame.
/// </para>
/// </remarks>
public static class WaitingOnThePerson
{
    /// <summary>The exchanges an agent is waiting on the person for, one edge each.</summary>
    /// <param name="waiting">Every edge in the project.</param>
    /// <param name="me">The person's seat.</param>
    /// <returns>The edges waiting on the person.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="waiting"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WaitingEdge> OnMe(IReadOnlyList<WaitingEdge> waiting, AgentName me)
    {
        ArgumentNullException.ThrowIfNull(waiting);

        return [.. waiting.Where(edge => edge.WaitedOn == me && edge.Waiter != me).DistinctBy(edge => edge.ExchangeId)];
    }

    /// <summary>Counts what waits on the person in one project.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="me">The person's seat.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many exchanges wait on the person's answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="broker"/> is <see langword="null"/>.</exception>
    public static async Task<int> CountAsync(BrokerClient broker, AgentName me, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        return OnMe(await broker.WaitingAsync(cancellationToken).ConfigureAwait(false), me).Count;
    }

    /// <summary>Reads what is waiting on the person in one project, each as the agent's latest message in that exchange.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="me">The person's seat.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The messages, oldest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="broker"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The wait-for graph says which exchanges an agent is waiting on the person for, and only the conversation says what was
    /// asked, so the conversations those exchanges are in are read and <see cref="WaitingOnYou"/> picks the questions out of
    /// them, the derivation the conversation page and the room use.
    /// </para>
    /// <para>
    /// Nothing is read when nothing waits on the person, which is the common case and costs one call. Nothing here
    /// collects, so reading this marks nothing delivered.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<Delivered>> ReadAsync(
        BrokerClient broker, AgentName me, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        var onMe = OnMe(await broker.WaitingAsync(cancellationToken).ConfigureAwait(false), me);

        if (onMe.Count == 0)
        {
            return [];
        }

        // Only the conversations that hold a question waiting on the person, found by the exchange the wait-for graph names. Reading
        // every conversation with an open exchange whole made the cost follow the person's whole history, on every Inbox open and
        // every poll of a conversation page, as soon as one agent waited.
        var located = await Task.WhenAll(
            onMe.Select(edge => broker.LocateExchangeAsync(edge.ExchangeId, cancellationToken))).ConfigureAwait(false);

        // The summary holds the newest message each sender wrote in each exchange, which is all WaitingOnYou reads: the
        // agent's newest is what the person owes a reply to.
        var exports = await SummaryExports.ReadAsync(
            broker, [.. located.OfType<string>().Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);

        return
        [
            .. exports
                .SelectMany(export => WaitingOnYou.From(export, onMe, me)
                    .SelectMany(on => on.Questions)
                    .Where(question => question.At is not null)
                    .Select(question => export.Messages.FirstOrDefault(m => m.Id == question.At))
                    .OfType<Message>())
                .OrderBy(message => message.CreatedUtc)
                .Select(message => new Delivered(message)),
        ];
    }
}
