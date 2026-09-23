namespace Parley.Core;

/// <summary>
/// Where a conversation stands (SPEC.md §5.0).
/// </summary>
/// <remarks>
/// Three states rather than two, because a poll in progress is genuinely a third thing: the work is
/// not over, but a closure question is outstanding and a new Request has to void it along with every
/// vote cast.
/// </remarks>
public enum ConversationState
{
    /// <summary>In progress, with no closure poll outstanding.</summary>
    Open = 0,

    /// <summary>A participant has proposed closure and the poll is running.</summary>
    /// <remarks>
    /// Reverts to <see cref="Open"/> when a Request is admitted into the conversation, discarding the
    /// poll and every vote, because a participant that was finished may not be once somebody reopens
    /// the subject.
    /// </remarks>
    Closing = 1,

    /// <summary>Closed, by consensus or by the person. Terminal.</summary>
    /// <remarks>
    /// A Request naming a closed conversation is refused. Work worth resuming after a conversation
    /// closed is new work, and starts a new conversation.
    /// </remarks>
    Closed = 2
}
