namespace Parley.Core;

/// <summary>
/// Where a conversation stands, per SPEC.md §5.0.
/// </summary>
/// <remarks>
/// Three states rather than two, because a poll in progress is genuinely a third thing: the
/// discussion is not over, but a closure question is outstanding and new activity has to void
/// it. Collapsing that into <see cref="Open"/> would leave nowhere to record that votes already
/// cast must be discarded.
/// </remarks>
public enum ConversationState
{
    /// <summary>
    /// In progress, with no closure poll outstanding.
    /// </summary>
    Open = 0,

    /// <summary>
    /// A participant has proposed closure and the poll is running.
    /// </summary>
    /// <remarks>
    /// Reverts to <see cref="Open"/> when an exchange is opened in the conversation, which
    /// discards the poll and every vote already cast. SPEC.md §5.0 requires discarding the
    /// votes rather than merely re-polling, because an agent that said it was finished may not
    /// be once a peer reopens the subject.
    /// </remarks>
    Closing = 1,

    /// <summary>
    /// Concluded. Terminal.
    /// </summary>
    /// <remarks>
    /// A Question naming a closed conversation is refused (§7.1, §5.0). Reopening is not a
    /// special case because it is not a real one: a discussion worth resuming after everyone
    /// agreed it was over is a new discussion, and identifiers cost nothing.
    /// </remarks>
    Closed = 2
}
