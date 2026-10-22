using System.Globalization;
using Parley.Core;

namespace Parley.Client;

/// <summary>
/// What a person sends when they ask an agent what it owns (SPEC.md §17 question 10).
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, read by both surfaces.</b> An answer given to the terminal would have been
/// given to the web UI, because the question is the same question — and a promise about what an agent
/// was told is a promise nobody checks unless the words live in one place.
/// </para>
/// <para>
/// <b>It is an ordinary Request and adds nothing to the protocol.</b> Sent from the person's seat
/// through <c>SeatVoice.RequestAsync</c>, opening one ordinary exchange in one conversation. There is
/// no new operation, no new message type, and nothing in the payload that anything parses: §13 tells a
/// receiver a payload is inert data, and addressing is recorded beside a draft and removed at the send,
/// so what crosses is always prose.
/// </para>
/// <para>
/// <b>What a surface recognises is the conversation's identifier, not a marker.</b> An identifier is
/// metadata the protocol already carries and that <c>conversations()</c> and <c>export()</c> already
/// return, so finding the discussion later needs no directive smuggled into somebody's words.
/// </para>
/// </remarks>
public static class OwnershipAsk
{
    /// <summary>The prefix every ownership discussion's identifier begins with.</summary>
    public const string Prefix = "owns-";

    /// <summary>How the timestamp half of an identifier is spelled.</summary>
    /// <remarks>
    /// Sortable, and inside <c>ConversationName</c>'s character set. A timestamp at all because asking
    /// twice must not collide, and because when two discussions exist the later one produced the line
    /// somebody is reading.
    /// </remarks>
    public const string Stamp = "yyyyMMdd-HHmmss";

    /// <summary>
    /// The ask, which tells the agent what the answer is for before asking for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every paragraph answers a requirement and none of it is padding. It says the answer becomes one
    /// line read by every agent on every roster listing and charged against each of them; that
    /// nothing is routed by it automatically, so the agent has no reason to over-claim; that
    /// the answer has two parts, the second naming a peer; and that nothing it says changes
    /// any configuration by itself, which is true and keeps it from reporting that it set something
    /// (Principle III).
    /// </para>
    /// <para>
    /// <b>Long for an ask, and still one ask.</b> Rule 8.1 wants one thing asked plainly with what it
    /// is for; this asks for a description of a boundary, in two halves that are one question.
    /// </para>
    /// <para>
    /// <b>The words live in <see cref="Facilitation"/></b>, because the facilitator sends them from the
    /// broker, which cannot reference this assembly. This is the text for when a person decides; the facilitator
    /// sends <see cref="Facilitation.AcceptedAsWritten"/> instead while Auto Accept is on.
    /// </para>
    /// </remarks>
    public static string Text { get; } = Facilitation.ReadByAPerson;

    /// <summary>What to say before asking more than one agent.</summary>
    /// <param name="agents">How many will be asked.</param>
    /// <returns>The cost, in the words a person reads before it is paid.</returns>
    /// <remarks>
    /// Shown and answered <em>before the first Request is sent</em>, because the cost of a bulk ask is
    /// a turn per agent and a person cannot un-spend it.
    /// </remarks>
    public static string Costs(int agents) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         This asks {agents} agent(s) what they own. Each one costs that agent a turn.
         They are asked separately, and answers arrive as they come.
         """);

    /// <summary>The identifier for a discussion about one agent's line.</summary>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="when">When the ask is being made.</param>
    /// <returns>An identifier a conversation may carry.</returns>
    /// <remarks>
    /// <para>
    /// The <em>role</em> rather than the whole agent name, because <c>@</c> is outside
    /// <see cref="ConversationName"/>'s character set — and the machine half would say nothing a
    /// reader of this identifier needs.
    /// </para>
    /// <para>
    /// <b>Derived and never typed.</b> A person naming this conversation themselves would be a person
    /// choosing whether their surface can find it again afterwards, which is not a choice worth
    /// offering.
    /// </para>
    /// </remarks>
    public static string Conversation(AgentName agent, DateTimeOffset when) =>
        ConversationName.Correct(
            Prefix + agent.Role + "-" + when.ToString(Stamp, CultureInfo.InvariantCulture));

    /// <summary>Whether an identifier names a discussion about what an agent owns.</summary>
    /// <param name="conversationId">The identifier.</param>
    /// <param name="agent">The agent it should be about, or null for any.</param>
    /// <returns>Whether a surface should read it as one.</returns>
    /// <remarks>
    /// <para>
    /// <b>An agent could open a conversation under this identifier itself, and it buys nothing.</b> A
    /// proposal binds nothing until a person accepts it, the person reads the transcript before
    /// accepting, and what they accept is what is on their screen. Recorded because a later reader
    /// would otherwise think it was missed.
    /// </para>
    /// <para>
    /// Matched on the prefix and the role rather than parsed, because the timestamp is for ordering and
    /// nothing reads it back.
    /// </para>
    /// </remarks>
    public static bool Recognises(string? conversationId, AgentName? agent = null)
    {
        if (string.IsNullOrEmpty(conversationId)
            || !conversationId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return agent is null
            || conversationId.StartsWith(
                Prefix + agent.Value.Role + "-", StringComparison.Ordinal);
    }
}
