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
/// Conversations this agent has been asked to conclude and has not yet answered, the oldest 200.
/// </param>
/// <param name="FileNotices">
/// Change notices about files this agent fetched (SPEC.md §9.5), at most a collection's worth, with a count of
/// the rest; <see langword="null"/> where nothing collected them.
/// </param>
/// <param name="RoadmapNoticeBatch">
/// What became of this agent's own roadmap proposals (SPEC.md §5.7), or <see langword="null"/> where nothing
/// collected them.
/// </param>
/// <param name="MessagesWaiting">
/// Messages still waiting after this collection, because a limit or the size one collection carries left them for
/// the next (SPEC.md §10.1).
/// </param>
/// <param name="ClosurePollsNotListed">
/// Closure polls outstanding beyond those this collection lists, which later ones list (SPEC.md §10.1).
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
    IReadOnlyList<ClosurePoll> ClosurePolls,
    FileNoticeBatch? FileNotices = null,
    IReadOnlyList<RoadmapNotice>? RoadmapNoticeBatch = null,
    int MessagesWaiting = 0,
    int ClosurePollsNotListed = 0)
{
    /// <summary>
    /// Gets what became of this agent's own roadmap proposals, collected with this inbox.
    /// </summary>
    /// <remarks>
    /// A fourth collection for the reason file notices are a third: it is not a message. It opens nothing, is owed
    /// nothing and wakes nobody, and carries the agent's own title rather than anybody's words (SPEC.md §5.7).
    /// </remarks>
    public IReadOnlyList<RoadmapNotice> RoadmapNotices => RoadmapNoticeBatch ?? [];

    /// <summary>
    /// Gets the change notices this collection handed over, and how many are left for a later one.
    /// </summary>
    /// <remarks>
    /// A third collection for the reason polls are a second: a notice is not a message (SPEC.md §9.5). It is
    /// deleted as it is handed over, like a message, but it owes nothing and wakes nothing, like a poll.
    /// </remarks>
    public FileNoticeBatch Notices => FileNotices ?? FileNoticeBatch.None;

    /// <summary>
    /// Gets a value indicating whether there is anything here to act on.
    /// </summary>
    /// <remarks>
    /// Named for what it means rather than derived from one collection, so a reader of only
    /// <see cref="Messages"/> cannot under-report an outstanding poll or a file that changed.
    /// </remarks>
    public bool IsEmpty => Messages.Count == 0 && ClosurePolls.Count == 0 && Notices.Notices.Count == 0 && RoadmapNotices.Count == 0;
}
