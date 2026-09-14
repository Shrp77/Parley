namespace Parley.Core;

/// <summary>
/// What one item of a Request came to, as its Result says (SPEC.md §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Four a receiver may write and one only the broker writes. <see cref="Closed"/> is recorded when
/// the conversation is closed before the receiver sent its Result, and it is attributed to the
/// broker: an outcome is a participant's judgement about its own work, and a close nobody party to
/// the work made must not read as though somebody had.
/// </para>
/// <para>
/// There is no outcome meaning "not mine". An agent asked for work another agent owns pursues it
/// inside the same conversation (§5.3), and reports <see cref="Blocked"/> only for a concrete
/// blocker it could not resolve.
/// </para>
/// </remarks>
public enum Outcome
{
    /// <summary>The work is finished in the receiver's repository and meets <c>done_when</c>.</summary>
    Done = 0,

    /// <summary>The question is answered.</summary>
    Answered = 1,

    /// <summary>Received and understood; nothing further to do or say.</summary>
    Acknowledged = 2,

    /// <summary>A concrete blocker remains after the receiver tried to resolve it.</summary>
    Blocked = 3,

    /// <summary>The conversation was closed before the receiver sent its Result. The broker's alone.</summary>
    Closed = 4
}
