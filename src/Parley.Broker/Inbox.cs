using Parley.Core;

namespace Parley.Broker;

/// <summary>
/// What an agent collects when it drains: messages addressed to it, and any closure poll
/// outstanding against it.
/// </summary>
/// <param name="Messages">
/// The messages delivered by this drain, debts first per SPEC.md §7.3, each paired with the
/// envelope §13 requires it to be delivered inside.
/// </param>
/// <param name="ClosurePolls">
/// Conversations this agent has been asked to conclude and has not yet answered.
/// </param>
/// <remarks>
/// <para>
/// The poll travels here rather than as a message because SPEC.md §5 admits three message types
/// and no others, and a fourth carrying the broker's questions would make the broker a
/// participant that others address — which it is not.
/// </para>
/// <para>
/// Two collections rather than one sequence of a common supertype, deliberately. A message is
/// delivered once and stamped so it is not handed over again, while a poll stays outstanding until
/// the participant answers it. Flattening them would put that difference behind a type test.
/// </para>
/// </remarks>
public sealed record Inbox(
    IReadOnlyList<DeliveredMessage> Messages,
    IReadOnlyList<ClosurePoll> ClosurePolls)
{
    /// <summary>
    /// Gets a value indicating whether there is anything here to act on.
    /// </summary>
    /// <remarks>
    /// Named for what it means rather than derived from one collection, so a reader of only
    /// <see cref="Messages"/> cannot under-report an outstanding poll.
    /// </remarks>
    public bool IsEmpty => Messages.Count == 0 && ClosurePolls.Count == 0;
}
