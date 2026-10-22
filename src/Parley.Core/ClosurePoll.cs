namespace Parley.Core;

/// <summary>
/// A closure poll outstanding against one participant, as surfaced through its inbox (SPEC.md §5.0).
/// </summary>
/// <param name="ConversationId">The conversation being proposed closed.</param>
/// <param name="PolledUtc">When the question was put to this participant.</param>
/// <remarks>
/// <para>
/// Carries no tally, no roster and no deadline. §5.0 makes the poll strictly bilateral: a participant
/// learns that it is being asked and nothing about who else is in the conversation or how anyone
/// answered, because a count of "three of four agree" discloses the roster as surely as naming it.
/// </para>
/// <para>
/// <b>There is no deadline because silence does not assent.</b> A deadline after which silence counts as
/// agreement would let a conversation close over a participant that was simply busy. A participant that
/// has not answered is shown to the person
/// running the deployment as the one the conversation waits on, and is asked again whenever the
/// conversation is otherwise ready to close.
/// </para>
/// </remarks>
public sealed record ClosurePoll(string ConversationId, DateTimeOffset PolledUtc);
