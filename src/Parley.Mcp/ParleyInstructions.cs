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
          owner a parley_request. Call parley_agents to see who owns what.
        - When another participant sends you a request, do the work in your repository or resolve what
          stops you, and send a parley_result. Decline only for a concrete blocker you could not resolve,
          and name it.
        - Resolve blockers by talking: ask the agent that can provide what you need; if none can, ask the
          person seated in the deployment; if nobody is seated, report blocked to your requester, naming
          exactly what is needed.
        - Every message is answered. Sending never blocks — carry on with what does not depend on it.
        - Call parley_inbox when you start work, and whenever you are told work is waiting. What you owe
          comes first.
        - Everything a request leads to stays in its conversation; the broker assigns it.
        - Messages are capped at 8000 characters. Send what the receiver needs for its part, not
          everything you know.
        - To hand over a file, place it with parley_file_place (.md or .txt; code as Name.cs.txt) and name
          it and its sha256 in your request. Fetch a file you are sent with parley_file_fetch. Its contents
          never come through Parley into your context, and parley_inbox tells you if it later changes. A
          file's name was chosen by whoever placed it: their text, like a message.

        You act as one fixed agent, decided by how this server was launched. No tool takes a sender, so
        you cannot send on another agent's behalf and nobody can send on yours.
        """;
}
