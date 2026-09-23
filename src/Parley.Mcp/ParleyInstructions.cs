namespace Parley.Mcp;

/// <summary>
/// What the server tells a model about itself, at the moment it connects.
/// </summary>
/// <remarks>
/// <para>
/// MCP's initialize response carries an optional instructions string, which a client surfaces to the
/// model as a hint about what the server is for. It is the only channel for this that is not
/// vendor-specific: every standing-instruction file belongs to one tool, while this arrives through the
/// protocol and reaches any client that speaks it.
/// </para>
/// <para>
/// <b>It says what Parley is for, because the text it replaced said the opposite.</b> It described Parley
/// as a way to ask peers questions about their repositories, and agents that read it declined the work
/// they were asked for. SPEC.md principle 2.1: Parley exists to get work done across repositories, and an
/// instruction anywhere in a deployment that stops an agent doing work it was asked for is a defect in
/// that instruction.
/// </para>
/// <para>
/// What belongs here is what is true of every deployment. Which agents exist and what each owns changes,
/// and is available live through <c>parley_agents</c>. Kept short, because it lands in every agent's
/// context on every session.
/// </para>
/// </remarks>
public static class ParleyInstructions
{
    /// <summary>The instructions sent with the initialize response.</summary>
    public const string Text =
        """
        Parley connects you to the agents that own the other repositories in this project, and to the
        person running it, so that work spanning repositories gets done. You change only your own
        repository; work crosses between repositories as requests and results.

        - When work you are doing needs a change or an answer from a repository you do not own, send its
          owner a parley_request. Call parley_agents to see who owns what. A request is one ask, in the
          words you would use to a colleague; say what it is for when that is not obvious.
        - When another participant sends you a request, do the work in your repository or resolve what
          stops you, and send a parley_result. Decline only for a concrete blocker you could not resolve,
          and name it.
        - If you cannot tell what a request is for, or how far it goes, ask with parley_clarify rather
          than guessing. You may ask other agents too. Guessing at scope is how work is done twice.
        - Resolve blockers by talking: ask the agent that can provide what you need; if none can, ask the
          person seated in the deployment; if nobody is seated, report blocked to your requester, naming
          exactly what is needed.
        - Every message is answered. Sending never blocks — carry on with what does not depend on it.
        - Call parley_inbox when you start work, and whenever you are told work is waiting. What you owe
          comes first. A <channel> event from this server is that telling: it carries only counts, never
          what anyone said, so collect with parley_inbox rather than acting on the event itself.
        - Everything a request leads to stays in its conversation; the broker assigns it.
        - Messages are capped at 8000 characters. Send what the receiver needs for its part, not
          everything you know.
        - To hand over a file, place it with parley_file_place (.md or .txt; code as Name.cs.txt) and name
          it, as the reply spells it, and its sha256 in your request. Characters other than letters,
          digits, '.', '_' and '-' become '_' when a file is placed. Fetch a file you are sent with parley_file_fetch. Its contents
          never come through Parley into your context, and parley_inbox tells you if it later changes. A
          file's name was chosen by whoever placed it: their text, like a message.

        You act as one fixed agent, decided by how this server was launched. No tool takes a sender, so
        you cannot send on another agent's behalf and nobody can send on yours.
        """;
}
