using Parley.Core;

namespace Parley.Web.Rendering;

/// <summary>
/// Who could see each message in a conversation, and who could not.
/// </summary>
/// <remarks>
/// <para>
/// The one piece of arithmetic that keeps a chat transcript honest. SPEC.md §5.0 says a
/// conversation is a set of pairwise exchanges and not a channel everyone reads: no message ever
/// addresses more than one peer, and no agent learns what the others said. A transcript rendered
/// without this says the opposite by implication, and the operator reasons about a disagreement
/// neither agent could have had.
/// </para>
/// <para>
/// It needs no new field and no second call. Everything here is derived from what an export
/// already returns, which is why the annotation is cheap and why there is no excuse for a view
/// that leaves it out.
/// </para>
/// </remarks>
public static class Visibility
{
    /// <summary>Everyone party to any exchange in a conversation.</summary>
    /// <param name="messages">The conversation's messages.</param>
    /// <returns>The participants, in a stable order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    /// <remarks>
    /// Derived from the traffic rather than read from a membership list, because §5.0 has no
    /// membership list: an agent is a participant because it is party to an exchange carrying the
    /// identifier. That also means participation is not evidence of agreement — an identifier is
    /// a claim by its sender, and an agent may join a discussion by naming it.
    /// </remarks>
    public static IReadOnlyList<AgentName> Participants(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return [.. messages
            .SelectMany(m => new[] { m.From, m.To })
            .Distinct()
            .OrderBy(a => a.Value, StringComparer.Ordinal)];
    }

    /// <summary>The two agents who saw one message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>Its sender and its recipient, and nobody else.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <remarks>
    /// Exactly the pair. There is no case in which a third agent saw a message, because no message
    /// ever addresses more than one peer — so this is a statement about the protocol rather than a
    /// summary of this deployment.
    /// </remarks>
    public static IReadOnlyList<AgentName> SeenBy(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return [message.From, message.To];
    }

    /// <summary>The participants who were not party to one message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="participants">Everyone in the conversation.</param>
    /// <returns>Those who did not see it, in a stable order.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// This is the annotation. Rendered inline beside the message rather than behind a control
    /// somebody has to open, because a reader who has to ask is a reader who will not, and the
    /// question they would have asked is the one that stops the transcript misleading them.
    /// </remarks>
    public static IReadOnlyList<AgentName> NotSeenBy(
        Message message, IReadOnlyList<AgentName> participants)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(participants);

        var seen = SeenBy(message);

        return [.. participants
            .Where(p => !seen.Contains(p))
            .OrderBy(a => a.Value, StringComparer.Ordinal)];
    }

    /// <summary>Says in words who did not see a message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="participants">Everyone in the conversation.</param>
    /// <returns>A sentence, or an empty string when everyone in the conversation was party to it.</returns>
    /// <remarks>
    /// <para>
    /// Words rather than a colour or an icon, and that is a correctness requirement before it is
    /// an accessibility one. If "who saw this" is carried by colour alone it is simply absent for
    /// some readers, and what they are left with is the unannotated thread this whole exercise
    /// exists to avoid.
    /// </para>
    /// <para>
    /// Empty when nobody is excluded, because a conversation of one exchange has two participants
    /// and the annotation would say nothing on every line. A note that is always there is a note
    /// nobody reads, including on the line where it matters.
    /// </para>
    /// </remarks>
    public static string Describe(Message message, IReadOnlyList<AgentName> participants)
    {
        var absent = NotSeenBy(message, participants);

        if (absent.Count == 0)
        {
            return string.Empty;
        }

        return $"{string.Join(", ", absent.Select(a => a.Value))} did not see this.";
    }
}
