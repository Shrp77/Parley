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
    /// <summary>Registered, and never activated: no session has ever attached.</summary>
    /// <remarks>
    /// The resting state of a repository somebody has configured and not yet opened. It is the
    /// only status that survives being read from storage unchanged — every other one below is
    /// derived from how long ago the agent was last heard from, because a stored liveness is a
    /// claim that nothing refreshes when the thing it describes dies.
    /// </remarks>
    Offline = 0,

    /// <summary>Sweeping normally: heard from within the last few sweeps.</summary>
    Live = 1,

    /// <summary>
    /// Has missed its recent sweeps but is not yet written off.
    /// </summary>
    /// <remarks>
    /// The state that did not exist, and whose absence made <see cref="Live"/> a lie. A session
    /// that is killed, crashes, or loses its machine never writes anything again — the old
    /// design had nothing demote it, so its row read <c>live</c> for ever with a frozen
    /// timestamp beside it that only the supervisor ever compared. This is the window in which
    /// a peer can tell "answering slowly" from "gone", and it is short on purpose: the useful
    /// question is whether to keep waiting, and that question expires.
    /// </remarks>
    NotResponding = 2,

    /// <summary>
    /// Silent long enough to stop routing to: five minutes without a sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a worse <see cref="Offline"/>. Offline is a repository nobody has opened
    /// yet and must stay routable — refusing traffic to it would refuse the very traffic meant
    /// to wake it. This says something else: it was here, and it stopped.
    /// </para>
    /// <para>
    /// Questions to such an agent are refused at admission, because accepting them costs a full
    /// TTL each and tells the asker nothing. Resolving what is already outstanding is the
    /// operator's decision: five minutes of silence is a laptop lid as often as a deleted
    /// repository, and force-closing real questions to save a human a judgement is the wrong
    /// trade.
    /// </para>
    /// </remarks>
    Unreachable = 3,

    /// <summary>
    /// Gone for good: the workspace no longer exists, or a person said so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are deliberate and the first is new. §14 used to reserve this for a person
    /// alone, on the ground that a missing directory is frequently temporary — a re-clone, a
    /// moved worktree, an unmounted drive — and that a broker tombstoning one would need a human
    /// to undo it. That reasoning is recorded rather than deleted, because it is the cost of
    /// this decision rather than an argument against having taken it: a repository that comes
    /// back needs <c>parley reinstate</c>, and will not recover on its own.
    /// </para>
    /// <para>
    /// What it buys is that a registry stops describing repositories that are not there.
    /// <see cref="Unreachable"/> already covers silence; this covers absence, which is a
    /// different observation and one the broker can make directly by looking.
    /// </para>
    /// <para>
    /// It is the only status that is stored rather than derived, because it is a judgement about
    /// the future rather than an observation of the past, and no timestamp implies it.
    /// </para>
    /// </remarks>
    Retired = 4
}
