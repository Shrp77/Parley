using Parley.Core;

namespace Parley.Mcp;

/// <summary>
/// Thrown when the agent a server was launched for has been retired, by a person or by its node finding its workspace gone.
/// </summary>
/// <remarks>
/// <para>
/// A type of its own rather than an <see cref="InvalidOperationException"/> carrying a message,
/// because the caller has to act on it: this is the one start-up failure that is neither a
/// broken environment nor a broken database, and it exits with a different code. Distinguishing
/// it by matching on message text would work until somebody improved the wording.
/// </para>
/// <para>
/// It is also the one start-up failure that was decided rather than suffered: a person retired the agent, or its node
/// found its workspace gone (a repository put back afterwards meets exactly this). SPEC.md §14 makes
/// retirement clearable only by an explicit reinstatement — never by registration, and starting
/// a server is the closest thing to registration there is — so the server has to stop rather
/// than quietly work around the withdrawal it just discovered.
/// </para>
/// </remarks>
public sealed class AgentRetiredException : Exception
{
    /// <summary>Initialises the exception for a named agent.</summary>
    /// <param name="agent">The agent whose registration was withdrawn.</param>
    public AgentRetiredException(AgentName agent)
        : base($"'{agent.Value}' has been retired and cannot attach: a person retired it, or its node found its " +
               "workspace gone. Retirement is cleared only by reinstating it (the terminal's agents list, Alt+i), " +
               "never by starting a server.") =>
        Agent = agent.Value;

    /// <summary>
    /// The retired agent's name.
    /// </summary>
    /// <remarks>
    /// The single constructor requires one, so this is never absent. The conventional
    /// message-only and message-with-inner overloads are deliberately not here: nothing throws
    /// this except the attach path, an exception carrying no agent could not say which
    /// repository to look at, and a public constructor nobody calls is API to maintain in
    /// exchange for a convention.
    /// </remarks>
    public string Agent { get; }
}
