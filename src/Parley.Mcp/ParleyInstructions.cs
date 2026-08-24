namespace Parley.Mcp;

/// <summary>
/// What the server tells a model about itself, at the moment it connects.
/// </summary>
/// <remarks>
/// <para>
/// MCP's initialize response carries an optional instructions string, which a client surfaces to
/// the model as a hint about what the server is for. It is the only channel for this that is not
/// vendor-specific: <c>CLAUDE.md</c>, <c>AGENTS.md</c> and every other standing-instruction file
/// belongs to one tool, while this arrives through the protocol and reaches any client that
/// speaks it.
/// </para>
/// <para>
/// That matters because binding the tools tells a model <em>how</em> to ask and not <em>that
/// there is anyone to ask</em>. Without this, every deployment had to explain Parley itself in a
/// file of its client's choosing, and one that did not got an agent which answered from its own
/// repository and guessed at the rest — the failure this whole protocol exists to prevent,
/// reached by installing it correctly and stopping one step early.
/// </para>
/// <para>
/// <b>What belongs here is what is true of every deployment.</b> Which peers exist and which
/// boundaries this repository does not own are facts about one project, and they change; they
/// belong in the repository's own instructions, and the roster is available live through
/// <c>parley_agents</c> in any case. Baking a roster into a string sent once at start-up would
/// be stale by the first registration.
/// </para>
/// <para>
/// Kept short on purpose, and the purpose is the same one that caps a message at a few thousand
/// characters: this text lands in the context of every agent on every session, so it is a
/// standing charge against exactly the budget the protocol exists to protect. It says what a
/// model cannot infer from the tool descriptions — when to reach for Parley at all, and what it
/// costs — and leaves the mechanics to the tools, which describe themselves.
/// </para>
/// </remarks>
public static class ParleyInstructions
{
    /// <summary>The instructions sent with the initialize response.</summary>
    public const string Text =
        """
        Parley connects you to agents working in other repositories, as peers. You cannot see
        their files, their history or their context, and they cannot see yours: only the text of
        a question and its answer crosses between you.

        Use it when an answer depends on a repository you do not own. Inventing another
        codebase's behaviour is the mistake this exists to prevent, and asking costs one message.

        - Call parley_agents to see who exists and what each one owns. The roster changes as
          agents come and go, and it is how you pick who to ask.
        - Nothing arrives unprompted. Call parley_inbox when you start work, and again later if
          you are waiting on a reply.
        - Asking never blocks. If nobody ever answers you are told so, rather than left waiting.
        - A question must state what you will do with each possible answer. One that does not is
          refused: an answerer who knows what turns on the reply gives a better one.
        - Messages are capped at 4000 characters. What you send lands in somebody else's working
          memory, so say less than you want to.

        You act as one fixed agent, decided by how this server was launched. No tool takes a
        sender, so you cannot ask on another agent's behalf and nobody can ask on yours.
        """;
}
