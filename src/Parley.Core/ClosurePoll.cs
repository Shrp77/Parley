namespace Parley.Core;

/// <summary>
/// A closure poll outstanding against one agent, as surfaced through its inbox.
/// </summary>
/// <param name="ConversationId">The discussion being concluded.</param>
/// <param name="PolledUtc">When the question was put to this agent.</param>
/// <param name="ExpiresUtc">
/// When silence will be taken as assent, per SPEC.md §5.0.
/// </param>
/// <remarks>
/// In the domain rather than the store, beside <see cref="Message"/> and for the same reason:
/// it is one of the two things SPEC.md §10.1 says <c>inbox()</c> returns, so it is a protocol
/// concept an agent is shown rather than a shape the database happens to have. The records that
/// stayed in the store — a conversation's own state, an agent's registration — are read by the
/// broker and never by a participant.
/// </remarks>
/// <remarks>
/// Carries no tally and no roster, which is the point rather than an omission. §5.0 makes the
/// poll strictly bilateral: a participant learns that it is being asked and nothing about who
/// else is in the conversation, how many there are, or how anyone answered. A count of
/// "three of four agree" would disclose the roster size as surely as naming it.
/// <para>
/// <see cref="ExpiresUtc"/> is not a disclosure of that kind. It says when <em>this</em> agent's
/// own silence starts counting as agreement, which it needs in order to decide whether to
/// answer, and it is derived from the agent's own <see cref="PolledUtc"/> rather than from
/// anything about the others.
/// </para>
/// </remarks>
public sealed record ClosurePoll(
    string ConversationId,
    DateTimeOffset PolledUtc,
    DateTimeOffset ExpiresUtc);
