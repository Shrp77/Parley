using Parley.Client.Rendering;
using Parley.Core;

namespace Parley.Client;

/// <summary>
/// What an agent said it owns, read back out of the discussion it said it in.
/// </summary>
/// <param name="Message">The agent's own message, so a surface can point at it rather than restate it.</param>
/// <param name="Statement">Its readable text, which is what a person accepts or edits.</param>
/// <remarks>
/// <para>
/// <b>Agent-written text, and a surface must draw it as such.</b> FR-013 wants it attributed and
/// distinguishable from what the surface wrote, which is why the message travels with the text: the
/// sender, the time and the exchange are all facts a reader needs in order to treat the words as
/// somebody's rather than as the screen's.
/// </para>
/// <para>
/// <b>It binds nothing.</b> An answer arriving changes no configuration (FR-012); a person accepting it
/// is what writes a launch configuration, and even that does not write the registry.
/// </para>
/// </remarks>
public sealed record OwnershipProposal(Message Message, string Statement)
{
    /// <summary>The proposal a person should be shown, or null when the agent has said nothing.</summary>
    /// <param name="transcript">The conversation, in any order.</param>
    /// <param name="agent">Whose proposal to find.</param>
    /// <returns>The latest thing that agent said, with its text, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>The latest, not the first, and that is 0.30.1's rule reused for its own reason.</b> An
    /// exchange can hold a Request, a Clarify and then a revision, so an agent that argued and then
    /// changed its mind has said two statements — and the one a person accepts is the later. Taking the
    /// first would put the sentence they talked the agent out of in front of them.
    /// </para>
    /// <para>
    /// <b>Ordered by when it was written rather than by identifier</b>, because an identifier is the
    /// store's and a reader is asking about time. Ties break on the identifier, which is monotonic, so
    /// two messages written in the same tick still have one answer.
    /// </para>
    /// <para>
    /// An answer that is a question, an apology or a refusal comes back as it was written. This does
    /// not try to decide whether prose is a statement of ownership: a person reads what came back, and
    /// a surface that guessed would be hiding the case where an agent declined.
    /// </para>
    /// </remarks>
    public static OwnershipProposal? Latest(IEnumerable<Message>? transcript, AgentName agent)
    {
        if (transcript is null)
        {
            return null;
        }

        var said = transcript
            .Where(m => m.From == agent)
            .OrderByDescending(m => m.CreatedUtc)
            .ThenByDescending(m => m.Id)
            .FirstOrDefault();

        if (said is null)
        {
            return null;
        }

        var statement = Spoken(said);

        return statement.Length == 0 ? null : new OwnershipProposal(said, statement);
    }

    /// <summary>The readable text of a message, whatever shape its payload has.</summary>
    /// <param name="message">The message.</param>
    /// <returns>What it says, or empty when nothing could be read.</returns>
    /// <remarks>
    /// A Request and a Clarify carry their words in one field; a Result carries them against its
    /// outcomes, since the answer to an exchange is recorded per item. Asked of
    /// <see cref="Payload"/> rather than parsed here, so this agrees with what the transcript
    /// draws — two readers of one payload is two ideas of what an agent said.
    /// </remarks>
    private static string Spoken(Message message)
    {
        var payload = Payload.Read(message.PayloadJson);

        if (payload.Said.Length > 0)
        {
            return payload.Said.Trim();
        }

        var spoken = payload.Points
            .Select(point => point.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return spoken.Count == 0 ? string.Empty : string.Join(" ", spoken).Trim();
    }
}
