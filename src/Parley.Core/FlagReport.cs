namespace Parley.Core;

/// <summary>
/// Work in front of the person running the deployment: SPEC.md §10.2's <c>flags()</c>.
/// </summary>
/// <param name="Exchanges">What needs the person: open exchanges that have gone quiet or are owed by an agent that is
/// gone, a conversation waiting only on a participant who has not voted, and a turn stopped by a refused permission.</param>
/// <param name="Cycles">Cycles of Requests recorded in conversations that still have work open.</param>
/// <remarks>
/// <para>
/// <b>Flagging changes nothing about the work.</b> Nothing ends a piece of work
/// but its participants or its person (principle 2.8), so an exchange that stops moving is shown to
/// the person — naming the stage, the participant it waits on and since when — and the person, who
/// can see why, starts the agent, asks it, or closes the conversation.
/// </para>
/// <para>
/// Derived at read time from the thresholds each exchange carries (§6), never stored: an exchange
/// that moves is simply absent from the next read.
/// </para>
/// </remarks>
public sealed record FlagReport(IReadOnlyList<Flag> Exchanges, IReadOnlyList<RecordedCycle> Cycles)
{
    /// <summary>Gets a value indicating whether nothing needs the person's attention.</summary>
    /// <value><see langword="true"/> when both lists are empty.</value>
    public bool IsEmpty => (Exchanges?.Count ?? 0) == 0 && (Cycles?.Count ?? 0) == 0;
}

/// <summary>Why an open exchange is in front of the person.</summary>
public enum FlagKind
{
    /// <summary>Nobody has picked up what is owed: never started, machine off, or died on the way.</summary>
    NotCollected = 0,

    /// <summary>Picked up, and nothing from the owing participant for longer than the threshold.</summary>
    NoProgress = 1,

    /// <summary>Owed by an agent a person retired, so nobody is going to do it (§14).</summary>
    Retired = 2,

    /// <summary>
    /// A conversation being polled whose work is all closed and nobody has declined, so it is waiting only on
    /// this participant saying whether it is finished (§5.0).
    /// </summary>
    /// <remarks>
    /// Flagged at once rather than after a threshold: there is no work left to be patient with, and a
    /// participant that finished its part has no reason to come back and vote unless something asks it.
    /// </remarks>
    NotVoted = 3,

    /// <summary>
    /// The owing agent's last turn ended with tool calls refused for want of permission, and it has said nothing in the
    /// conversation since (SPEC.md §6).
    /// </summary>
    /// <remarks>
    /// Flagged at once, like <see cref="NotVoted"/> and for the opposite reason: there the work is done, here it cannot be
    /// done without a person. An agent running with nobody at the keyboard has a call needing approval refused rather than
    /// paused, and nothing it can do gets it past that.
    /// </remarks>
    PermissionRefused = 4
}

/// <summary>One piece of open work, or one closure poll, needing the person's attention.</summary>
/// <param name="Kind">Why.</param>
/// <param name="ExchangeId">The exchange; zero for <see cref="FlagKind.NotVoted"/>, which waits on no exchange.</param>
/// <param name="ConversationId">The conversation it belongs to.</param>
/// <param name="WaitingOn">The participant that owes the next move, or the vote.</param>
/// <param name="Waiting">The participant waiting on it: for a vote, one that has said it is finished.</param>
/// <param name="SinceUtc">When the silence began: the message not collected, the last sign of progress, or the conversation becoming ready to close.</param>
public sealed record Flag(
    FlagKind Kind,
    long ExchangeId,
    string ConversationId,
    AgentName WaitingOn,
    AgentName Waiting,
    DateTimeOffset SinceUtc);

/// <summary>A cycle of Requests admitted into a conversation (§7.2).</summary>
/// <param name="ExchangeId">The exchange whose Request closed the cycle.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Path">The participants in the order each waits on the next.</param>
/// <param name="RecordedUtc">When it was admitted.</param>
public sealed record RecordedCycle(
    long ExchangeId,
    string ConversationId,
    IReadOnlyList<AgentName> Path,
    DateTimeOffset RecordedUtc);
