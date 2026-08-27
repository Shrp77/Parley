using Parley.Core;

namespace Parley.Server;

/// <summary>
/// An operation named an agent that has no open <c>Attach</c> stream.
/// </summary>
/// <remarks>
/// <para>
/// Not an authorisation failure, and it must not be mistaken for one. SPEC.md §16 states plainly
/// that anything reaching the broker can claim to be any agent, so this proves nothing about who
/// is calling — it says only that no stream is open under that name, which is a fact about
/// sequence rather than about identity.
/// </para>
/// <para>
/// It exists because a broker is bound to the agent it attached for: its ghosting clock is when
/// that agent came up and its detach releases that agent's work. There is no broker to run an
/// operation against until a stream has opened one, and inventing one for the duration of a
/// single call would give it a start time of now — which is the value the ghosting grace of §6
/// is measured from.
/// </para>
/// </remarks>
public sealed class NotAttachedException : InvalidOperationException
{
    /// <summary>Creates the exception for an agent with no stream.</summary>
    /// <param name="agent">The agent that was named.</param>
    public NotAttachedException(AgentName agent)
        : base($"'{agent.Value}' is not attached. Open an Attach stream before calling anything " +
               "else: it is what registers the agent, releases whatever it abandoned, and holds " +
               "its place until it goes away.") =>
        Agent = agent;

    /// <summary>Creates the exception with a default message.</summary>
    public NotAttachedException()
        : base("The agent is not attached.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public NotAttachedException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public NotAttachedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the agent that was named, if one was.</summary>
    public AgentName? Agent { get; }
}
