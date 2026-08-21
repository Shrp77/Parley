namespace Parley.Core;

// SPEC.md §11.1 and §14. Here rather than in the store, beside ExchangeState and
// ConversationState, because these are protocol vocabulary rather than a shape the
// database happens to have: a tier decides how the broker routes, and a status decides
// whether it routes at all. The store persists them; it does not define them.

/// <summary>
/// How an agent attaches to the broker, classified by capability rather than by vendor.
/// </summary>
/// <remarks>
/// The broker degrades gracefully across tiers: a question to a pull-only agent is queued
/// rather than pushed, and a question to a headless agent triggers an invocation.
/// </remarks>
public enum AgentTier
{
    /// <summary>
    /// Native tool calls outbound, and inbound messages pushed into a running turn. The agent
    /// can be interrupted by an arriving message.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Native tool calls outbound, but inbound messages only seen when the agent drains its
    /// inbox. It cannot be interrupted.
    /// </summary>
    Pull = 1,

    /// <summary>
    /// No persistent session. The broker invokes the responder with a one-shot prompt and
    /// captures the reply.
    /// </summary>
    /// <remarks>
    /// The responder need not be an agent at all — a plain model API call, a local model, a
    /// script that queries a contracts directory, or a human with a notification all qualify.
    /// Anything that accepts a bounded question and returns text can participate.
    /// </remarks>
    Headless = 2
}

/// <summary>
/// The registration and liveness state of an agent.
/// </summary>
public enum AgentStatus
{
    /// <summary>Known to the registry but never yet connected.</summary>
    Registered = 0,

    /// <summary>Currently attached to the broker and able to receive messages.</summary>
    Live = 1,

    /// <summary>
    /// Previously live, now disconnected. Its open exchanges are orphaned and its pair slots
    /// released.
    /// </summary>
    Offline = 2,

    /// <summary>
    /// Spawning was attempted and failed repeatedly, so the agent is not worth routing to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a worse <see cref="Offline"/>. Offline is the ordinary resting state
    /// of a lazily spawned agent between questions, and must stay routable — refusing traffic
    /// to an offline agent would refuse the very traffic meant to wake it. This says something
    /// else: the broker tried, and there was nothing there.
    /// </para>
    /// <para>
    /// The usual cause is a repository that was deleted, moved, or renamed while its agent was
    /// still registered. Questions to such an agent are refused at admission, because
    /// accepting them costs a full TTL each and tells the asker nothing. Resolving what is
    /// already outstanding is the operator's decision, not the broker's: a deleted repository
    /// is often temporary, and force-closing on the broker's initiative would discard real
    /// questions to save a human a judgement they are better placed to make.
    /// </para>
    /// </remarks>
    Unreachable = 3,

    /// <summary>
    /// Retired by an operator: the repository is gone for good.
    /// </summary>
    /// <remarks>
    /// The only status the broker never sets on its own evidence. <see cref="Unreachable"/> is
    /// the broker reporting that it tried and failed, which a re-clone or a restored backup can
    /// invalidate at any moment; this is a person saying it will not come back. Start-up clears
    /// every other status as stale liveness and leaves this one, because it is a judgement
    /// rather than an observation.
    /// </remarks>
    Retired
}
