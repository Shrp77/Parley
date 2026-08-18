namespace Parley.Core;

/// <summary>
/// The kind of message travelling between two agents.
/// </summary>
/// <remarks>
/// There are exactly four, and the set is closed by design. Agents do not exchange
/// pleasantries, so every additional type would be a new way to spend tokens without
/// producing a decision.
/// </remarks>
public enum MessageType
{
    /// <summary>
    /// Opens an exchange and claims the pair slot. Carries one or more sub-questions, the
    /// asker's intent, and what the asker will do with each possible answer.
    /// </summary>
    Question = 0,

    /// <summary>
    /// Requests or supplies detail within an already-open exchange.
    /// </summary>
    /// <remarks>
    /// Deliberately claims no slot of its own. If it did, an answerer needing detail could
    /// not ask for it, because the pair's only slot is held by the very question being
    /// clarified — a deadlock inside the deadlock fix.
    /// </remarks>
    Clarify = 1,

    /// <summary>
    /// Resolves and closes an exchange, releasing the pair slot.
    /// </summary>
    Answer = 2,

    /// <summary>
    /// A fire-and-forget event. Opens no exchange, claims no slot, and expects no reply.
    /// </summary>
    /// <remarks>
    /// Typically a contract change announcement. Expected to be the highest-volume traffic
    /// in a mature deployment.
    /// </remarks>
    Notify = 3
}
