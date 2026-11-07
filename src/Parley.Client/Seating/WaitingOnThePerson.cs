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
    /// asked, so the conversations the person is party to that have an open exchange are read and
    /// <see cref="WaitingOnYou"/> picks the questions out of them, the derivation the conversation page and the room use.
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

        var open = new List<string>();
        string? next = null;

        do
        {
            var page = await broker
                .ConversationsAsync(me, pageToken: next, includeArchived: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            open.AddRange(page.Items.Where(c => c.HasOpenExchange).Select(c => c.Id));
            next = page.NextPageToken;
        }
        while (next is { Length: > 0 });

        var exports = await Task.WhenAll(
            open.Select(id => broker.ExportAsync(id, cancellationToken))).ConfigureAwait(false);

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
