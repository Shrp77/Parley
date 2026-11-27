using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>What both surfaces read when the person picks entries to move, and where they could go (SPEC.md §5.0).</summary>
public static class MovePicking
{
    /// <summary>The words a surface shows beside an exchange that goes because it was one send to several.</summary>
    public const string SentTogether = "It was one message to several, which go together.";

    /// <summary>The messages as a reader sees them: a send to several agents is one entry, not one per agent.</summary>
    /// <param name="messages">The messages, in the order the broker recorded them.</param>
    /// <returns>Each entry, in that order.</returns>
    /// <remarks>
    /// A send to several agents is recorded as one Request each, because a refusal is per agent. The words, the sender and
    /// the conversation are the same, so adjacent Requests that match are one entry. Only adjacent ones: reordering messages
    /// to join them would misstate the order the broker recorded. The broker keeps no record of this, so a surface is the
    /// only thing that can keep such a send together when it moves.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<Message>> Turns(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var turns = new List<List<Message>>();

        foreach (var message in messages)
        {
            if (turns.Count > 0
                && turns[^1][0] is { Type: MessageType.Request } first
                && message.Type == MessageType.Request
                && first.From == message.From
                && string.Equals(first.PayloadJson, message.PayloadJson, StringComparison.Ordinal)
                && turns[^1].All(m => m.To != message.To))
            {
                turns[^1].Add(message);
            }
            else
            {
                turns.Add([message]);
            }
        }

        return [.. turns.Select(turn => (IReadOnlyList<Message>)turn)];
    }

    /// <summary>What was picked, with every other exchange of any send to several among it.</summary>
    /// <param name="messages">The conversation's messages.</param>
    /// <param name="picked">The exchanges picked.</param>
    /// <returns>The exchanges that go.</returns>
    public static IReadOnlyList<long> WithSendsToSeveral(IReadOnlyList<Message> messages, IReadOnlyCollection<long> picked)
    {
        ArgumentNullException.ThrowIfNull(picked);

        var whole = new HashSet<long>(picked);

        foreach (var turn in Turns(messages).Where(turn => turn.Count > 1 && turn.Any(m => picked.Contains(m.ExchangeId))))
        {
            whole.UnionWith(turn.Select(m => m.ExchangeId));
        }

        return [.. whole.Order()];
    }

    /// <summary>What was picked, with every other exchange of any send to several or Request to a role among it.</summary>
    /// <param name="export">The conversation.</param>
    /// <param name="picked">The exchanges picked.</param>
    /// <returns>The exchanges that go.</returns>
    public static IReadOnlyList<long> Whole(ConversationExport export, IReadOnlyCollection<long> picked)
    {
        ArgumentNullException.ThrowIfNull(export);

        var whole = new HashSet<long>(WithSendsToSeveral(export.Messages, picked));
        var groups = export.Exchanges.Where(e => e.RequestGroup is not null && whole.Contains(e.ExchangeId)).Select(e => e.RequestGroup).ToHashSet();

        whole.UnionWith(export.Exchanges.Where(e => groups.Contains(e.RequestGroup)).Select(e => e.ExchangeId));

        return [.. whole.Order()];
    }

    /// <summary>Every conversation entries could be moved to, archived ones and every page included.</summary>
    /// <param name="client">The project's broker.</param>
    /// <param name="source">The conversation they are in.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The conversations a move would not refuse.</returns>
    /// <remarks>Every page, because a conversation left off this list could not be chosen at all, moving back to an old source among them.</remarks>
    public static async Task<IReadOnlyList<ConversationSummary>> DestinationsAsync(
        BrokerClient client, string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var found = new List<ConversationSummary>();
        string? next = null;

        do
        {
            var page = await client
                .ConversationsAsync(pageToken: next, includeArchived: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            found.AddRange(page.Items.Where(c =>
                !c.Closed
                && c.Item is null
                && !string.Equals(c.Id, source, StringComparison.Ordinal)
                && !c.Id.StartsWith(Facilitation.ConversationPrefix, StringComparison.Ordinal)));

            next = page.NextPageToken;
        }
        while (next is { Length: > 0 });

        return found;
    }
}
