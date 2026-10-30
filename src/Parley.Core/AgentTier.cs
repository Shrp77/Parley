namespace Parley.Core;

// SPEC.md §11.1 and §14. Here rather than in the store, beside ExchangeState and
// ConversationState, because these are protocol vocabulary rather than a shape the
// database happens to have: a tier says how an agent learns of its mail, and a status
// decides whether it is reachable at all. The store persists them; it does not define them.

/// <summary>
/// How an agent attaches to the broker, classified by capability rather than by vendor.
/// </summary>
/// <remarks>
/// Nothing routes on it. Every message waits in the store until its receiver collects it, so a
/// question to a pull-only agent waits for its next collection, and one to a headless agent
/// for the turn its node next gives it.
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
/// What an operator has decided about a registration.
/// </summary>
/// <remarks>
/// <para>
/// Two values, because there are only two judgements a person makes: the registration stands,
/// or it is finished. How reachable an agent is is not a judgement anybody records; it is
/// observed on a connection, and lives in <see cref="AgentPresence"/>.
/// </para>
/// <para>
/// The separation is the point. A stored liveness has to be withdrawn by something, and the
/// case where withdrawal matters most — a session killed, crashed, or on a machine that lost
/// power — is exactly the case where nothing is left running to withdraw it.
/// </para>
/// </remarks>
public enum AgentStatus
{
    /// <summary>The registration stands.</summary>
    /// <remarks>
    /// Zero because it is what a row starts as, and because a default-constructed value that
    /// read as <see cref="Retired"/> would silently take an agent out of service.
    /// </remarks>
    Active = 0,

    /// <summary>An operator has said this agent is finished.</summary>
    /// <remarks>
    /// The one statement about an agent that is a judgement rather than an observation, and the
    /// only one durable enough to route on. Cleared only by an explicit reinstatement, never by
    /// registering again — a control an agent could clear by restarting is not a control.
    /// </remarks>
    Retired = 1
}

/// <summary>
/// How reachable an agent is, right now, read from connections rather than stored.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is derived from two facts a process knows for certain: whether the node on
/// that machine is connected to the broker, and whether the agent's own adapter is. Nothing is
/// inferred from a timestamp, which would let a session killed on Tuesday read as healthy on Friday.
/// </para>
/// <para>
/// None of these gate routing. See SPEC.md §14: a Request to an agent that is not running is
/// what causes it to be started, and one to an agent on a switched-off machine waits and is
/// flagged to the person — which a refusal at the door would hide.
/// </para>
/// </remarks>
public enum AgentPresence
{
    /// <summary>The machine is unreachable, so nothing can start this agent.</summary>
    /// <remarks>
    /// Zero because it is the honest reading of knowing nothing. A default that claimed an
    /// agent was running would be a claim made by a value nobody set.
    /// </remarks>
    Stranded = 0,

    /// <summary>The node is connected; nobody is running the agent, and the node can start it.</summary>
    /// <remarks>
    /// The ordinary state of a repository nobody has open, and not a fault. Most agents are here
    /// most of the time.
    /// </remarks>
    Startable = 1,

    /// <summary>The agent's adapter is connected and can be routed to now.</summary>
    Running = 2,

    /// <summary>The person seat only: a surface is open and in use (SPEC.md §14).</summary>
    /// <remarks>
    /// A person has no machine and is never started, so <see cref="Startable"/> would tell a peer an absent person can be
    /// woken and <see cref="Stranded"/> that one cannot be reached. Neither this nor <see cref="Away"/> gates routing.
    /// </remarks>
    Here = 3,

    /// <summary>The person seat only: nobody is at a screen; the seat is still there and still asked (SPEC.md §14).</summary>
    Away = 4
}

