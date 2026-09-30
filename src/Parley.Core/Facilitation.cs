using System.Globalization;

namespace Parley.Core;

/// <summary>
/// The facilitator: Parley's own participant in every project (SPEC.md principle 2.11, §3), and the words it asks.
/// </summary>
/// <remarks>
/// <para>
/// <b>It asks, and does nothing else.</b> It may ask a question, send a reply a person wrote, and propose and agree
/// that its own conversation is done. It holds no operator operation, is never the person seated, never receives
/// anybody's work, and never decides which conversation an agent's own work goes into. What it adds is <em>who</em>
/// asks, never what asking can do — one operation more and it would be the orchestrator the constitution forbids.
/// </para>
/// <para>
/// <b>Its identity is the one no launch fixes.</b> The broker writes its row for its own store, under a role that
/// storage refuses to every other adapter (§12.3). <c>parley</c> alone is deliberately <em>not</em> that role: this
/// repository registers as its own agent under it, and enrolment turns a folder called Parley into it.
/// </para>
/// <para>
/// <b>Here rather than beside either surface</b> because the broker sends the question and both surfaces show it.
/// The broker cannot reference <c>Parley.Client</c>, and a text kept in two places is a promise about what an agent
/// was told that nobody checks.
/// </para>
/// </remarks>
public static class Facilitation
{
    /// <summary>The reserved role. Storage refuses it to any adapter but <see cref="Adapter"/>.</summary>
    public const string Role = "parley-facilitator";

    /// <summary>The facilitator's adapter. Storage refuses it under any role but <see cref="Role"/>.</summary>
    public const string Adapter = "parley-facilitator";

    /// <summary>What every surface calls it.</summary>
    public const string DisplayName = "Parley";

    /// <summary>The attribution on the facilitator's own question (§11.3).</summary>
    public const string Attribution = "parley:facilitator";

    /// <summary>
    /// The attribution on a reply a person wrote and the facilitator sent (§5, §11.3). A person's words, shown as a
    /// person's and never as Parley's.
    /// </summary>
    public const string PersonAttribution = "parley:person";

    /// <summary>The prefix every ownership question's conversation begins with.</summary>
    /// <remarks>
    /// For a person reading a list of conversations. <b>Nothing recognises a question by it</b>: an agent can open a
    /// conversation under this prefix itself (research finding 11), so a question is the facilitator's own Request,
    /// which storage checks.
    /// </remarks>
    public const string ConversationPrefix = "owns-";

    /// <summary>How the timestamp half of a question's conversation is spelled.</summary>
    public const string Stamp = "yyyyMMdd-HHmmss";

    /// <summary>The facilitator's name in a store the broker runs on <paramref name="machine"/>.</summary>
    /// <param name="machine">The broker's machine, as <see cref="MachineName.Current"/> spells it.</param>
    /// <returns><c>parley-facilitator@machine</c>.</returns>
    public static AgentName On(string machine) => AgentName.Create(Role + "@" + machine);

    /// <summary>Whether a name is the facilitator's, on any machine.</summary>
    /// <param name="agent">The name.</param>
    /// <returns>Whether its role is the reserved one.</returns>
    public static bool Is(AgentName agent) => string.Equals(agent.Role, Role, StringComparison.Ordinal);

    /// <summary>Whether a role is the reserved one, so a door can refuse it before storage does.</summary>
    /// <param name="role">The role, as typed or derived.</param>
    /// <returns>Whether it is reserved.</returns>
    public static bool IsReserved(string? role) => string.Equals(role, Role, StringComparison.Ordinal);

    /// <summary>What a door says when it refuses the reserved role.</summary>
    public const string Reserved =
        "'" + Role + "' is the name Parley uses for itself in every project, so no agent or seat may take it. " +
        "Choose another name.";

    /// <summary>The conversation a question to <paramref name="agent"/> is asked in, before any collision.</summary>
    /// <param name="agent">Who is asked.</param>
    /// <param name="when">When.</param>
    /// <returns><c>owns-&lt;role&gt;-&lt;stamp&gt;</c>.</returns>
    /// <remarks>
    /// Legal by construction: a role is <c>[a-z][a-z0-9-]*</c> and the stamp is digits and a hyphen, all inside a
    /// conversation identifier's set. The machine half is left out because it may carry dots, and a run of dots is
    /// refused. So two agents of one role on two machines, asked in the same second, derive the same identifier —
    /// which is why the facilitator adds a suffix when the name is taken (<see cref="Numbered"/>).
    /// </remarks>
    public static string ConversationFor(AgentName agent, DateTimeOffset when) =>
        ConversationPrefix + agent.Role + "-" + when.ToString(Stamp, CultureInfo.InvariantCulture);

    /// <summary>The <paramref name="n"/>th alternative to a conversation identifier that is taken.</summary>
    /// <param name="conversation">The identifier.</param>
    /// <param name="n">From 2.</param>
    /// <returns>The identifier with <c>-n</c> appended.</returns>
    public static string Numbered(string conversation, int n) =>
        string.Create(CultureInfo.InvariantCulture, $"{conversation}-{n}");

    /// <summary>What the facilitator asks, chosen by the project's Auto Accept switch at the moment it asks.</summary>
    /// <param name="autoAccept">Whether an answer will become the agent's line with no person involved.</param>
    /// <returns>The question.</returns>
    /// <remarks>
    /// Two texts because 020's ends with a promise that Auto Accept breaks — "a person will read your answer … before
    /// anything is written" (research finding 10). A question that told an agent something untrue about what happens
    /// to its answer would be the facilitator misleading the one participant it exists to ask.
    /// </remarks>
    public static string Question(bool autoAccept) => autoAccept ? AcceptedAsWritten : ReadByAPerson;

    /// <summary>What both questions ask, and why.</summary>
    /// <remarks>
    /// Both keep 020's two halves — what the agent owns, and what it does not and who does — because the exclusion is
    /// the half nobody else can supply.
    /// </remarks>
    private const string Asked = """
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
        """;

    /// <summary>020's question, whose last paragraph is true when a person decides.</summary>
    public static string ReadByAPerson { get; } = Lines(Asked + """


        A person will read your answer, may disagree with it, and may argue about it with you
        before anything is written. Nothing you say here changes any configuration by itself.
        """);

    /// <summary>The question when Auto Accept is on, whose last paragraph says what will actually happen.</summary>
    public static string AcceptedAsWritten { get; } = Lines(Asked + """


        Your answer becomes your ownership line as you write it, and every agent in this project
        reads that line when deciding who to ask. Answer in one paragraph. A person can review and
        change it later.
        """);

    /// <summary>The same words with every line break a line feed, whatever the checkout's line endings are.</summary>
    /// <remarks>
    /// <b>A raw string literal takes its line breaks from the file it is written in</b>, and a Windows checkout with
    /// <c>core.autocrlf</c> — the default there — turns this file's into CRLF. Without this, the question an agent
    /// receives would depend on which machine built the broker, and so would its content hash.
    /// </remarks>
    private static string Lines(string text) => text.ReplaceLineEndings("\n");
}

/// <summary>Where one of the facilitator's questions stands (SPEC.md §10.2 <c>facilitation</c>).</summary>
public enum QuestionState
{
    /// <summary>Sent, and not yet answered.</summary>
    Asked = 0,

    /// <summary>The agent asked something back instead of answering, and a person must reply.</summary>
    AskedBack = 1,

    /// <summary>The agent answered, and nothing has been decided yet.</summary>
    Answered = 2,

    /// <summary>The answer is held for a person, with the reason.</summary>
    Waiting = 3,

    /// <summary>The answer became the agent's line.</summary>
    Accepted = 4,

    /// <summary>A person discarded the answer. Nothing was written.</summary>
    Discarded = 5
}

/// <summary>One question the facilitator asked, and what became of it.</summary>
/// <param name="Agent">Who was asked.</param>
/// <param name="Conversation">The conversation it was asked in.</param>
/// <param name="Exchange">The Request's exchange.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Answer">The agent's words as written, once it answered.</param>
/// <param name="Flattened">The answer with runs of whitespace collapsed, once it answered.</param>
/// <param name="WaitingBecause">Why it waits for a person, when it does.</param>
/// <param name="AskedUtc">When it was asked.</param>
/// <param name="SettledUtc">When it was accepted or discarded.</param>
public sealed record OwnershipQuestion(
    AgentName Agent,
    string Conversation,
    long Exchange,
    QuestionState State,
    string? Answer,
    string? Flattened,
    string? WaitingBecause,
    DateTimeOffset AskedUtc,
    DateTimeOffset? SettledUtc)
{
    /// <summary>Whether it is still being decided, so nothing asks again (§10.2).</summary>
    public bool Live => State is QuestionState.Asked or QuestionState.AskedBack
                             or QuestionState.Answered or QuestionState.Waiting;
}

/// <summary>What became of a person's action on a question: done, or why not.</summary>
/// <param name="Done">Whether it was done.</param>
/// <param name="Problem">Why not, in words a person reads, when it was not.</param>
public sealed record FacilitationAction(bool Done, string? Problem)
{
    /// <summary>It was done.</summary>
    public static FacilitationAction Did { get; } = new(true, null);

    /// <summary>It was not, and why.</summary>
    /// <param name="problem">Why.</param>
    /// <returns>The outcome.</returns>
    public static FacilitationAction Not(string problem) => new(false, problem);
}

/// <summary>What a person reviews ownership from: the switch, and every agent's current question.</summary>
/// <param name="AutoAccept">The project's Auto Accept switch.</param>
/// <param name="Questions">Each agent's latest question, if it was ever asked.</param>
public sealed record FacilitationReport(bool AutoAccept, IReadOnlyList<OwnershipQuestion> Questions);
