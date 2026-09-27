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
/// receiver a payload is inert data, and 017's chip rule is what has kept that true on these surfaces
/// — addressing is recorded beside a draft and removed at the send, so what crosses is always prose.
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
    /// The ask, which tells the agent what the answer is for before asking for it (FR-003, FR-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every paragraph answers a requirement and none of it is padding. It says the answer becomes one
    /// line read by every agent on every roster listing and charged against each of them (FR-003); that
    /// nothing is routed by it automatically, so the agent has no reason to over-claim (FR-030); that
    /// the answer has two parts, the second naming a peer (FR-004); and that nothing it says changes
    /// any configuration by itself, which is true and keeps it from reporting that it set something
    /// (Principle III).
    /// </para>
    /// <para>
    /// <b>Long for an ask, and still one ask.</b> Rule 8.1 wants one thing asked plainly with what it
    /// is for; this asks for a description of a boundary, in two halves that are one question.
    /// </para>
    /// </remarks>
    public const string Text = """
        What do you own in this deployment, and what do you not?

        I am asking because your answer becomes one line in the registry. Every agent in this
        deployment reads that line whenever anybody lists the roster, and it costs each of them
        context every time - so it is read far more often than it is written. It is what a peer
        uses to decide whether to send a piece of work to you or to somebody else. Nothing is
        routed by it automatically and nothing is refused because of it; it is advice to whoever
        is choosing who to ask.

        Please answer in two parts:

        1. What you own - the areas of work that are yours, in as few words as carry the meaning.
        2. What you do not own, especially anything a peer might reasonably assume is yours. If
           you believe another agent owns it, name the agent.

        The second part is the half nobody else can supply. A repository can be read to find out
        what it contains; it cannot be read to find out what it has decided not to own, and a
        person guessing is no better placed than a tool guessing.

        A person will read your answer, may disagree with it, and may argue about it with you
        before anything is written. Nothing you say here changes any configuration by itself.
        """;

    /// <summary>What to say before asking more than one agent (FR-007).</summary>
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
