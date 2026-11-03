using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;

namespace Parley.Web.Seating;

/// <summary>What a project's agents are waiting on the person for, across its conversations.</summary>
/// <remarks>One implementation for the Inbox and the conversation page, so the two cannot list different things.</remarks>
internal static class PersonOwes
{
    /// <summary>Reads what is waiting on the person, each as the agent's latest message in that exchange.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="me">The person's seat.</param>
    /// <param name="token">Cancels the reads.</param>
    /// <returns>The messages, oldest first, in the shape a page draws a message in.</returns>
    /// <remarks>
    /// <para>
    /// The wait-for graph says which exchanges an agent is waiting on the person for, and only the conversation says what was
    /// asked, so the conversations the person is party to that have an open exchange are read and
    /// <see cref="WaitingOnYou"/> picks the questions out of them, the derivation the conversation page and the terminal use.
    /// </para>
    /// <para>
    /// Nothing is read when nothing waits on the person, which is the common case and costs one call. Nothing here
    /// collects, so reading this marks nothing delivered.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<Delivered>> ReadAsync(BrokerClient broker, AgentName me, CancellationToken token)
    {
        var waiting = await broker.WaitingAsync(token).ConfigureAwait(false);
        var onMe = waiting.Where(edge => edge.WaitedOn == me && edge.Waiter != me).ToList();

        if (onMe.Count == 0)
        {
            return [];
        }

        var open = new List<string>();
        string? next = null;

        do
        {
            var page = await broker
                .ConversationsAsync(me, pageToken: next, includeArchived: true, cancellationToken: token)
                .ConfigureAwait(false);

            open.AddRange(page.Items.Where(c => c.HasOpenExchange).Select(c => c.Id));
            next = page.NextPageToken;
        }
        while (next is { Length: > 0 });

        var exports = await Task.WhenAll(
            open.Select(id => broker.ExportAsync(id, token))).ConfigureAwait(false);

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
