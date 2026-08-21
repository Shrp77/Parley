using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// What an agent collects when it drains: messages addressed to it, and any closure poll
/// outstanding against it.
/// </summary>
/// <param name="Messages">
/// The messages delivered by this drain, debts first per SPEC.md §7.3.
/// </param>
/// <param name="ClosurePolls">
/// Conversations this agent has been asked to conclude and has not yet answered.
/// </param>
/// <remarks>
/// <para>
/// The poll travels here rather than as a message because SPEC.md §5 admits four message types
/// and no others, and a fifth carrying broker questions would also make the broker a
/// participant that agents address — which it is not. §10.1 says as much in its description of
/// <c>inbox()</c>, and this is the shape that obeys it.
/// </para>
/// <para>
/// Two collections rather than one sequence of a common supertype, deliberately. They are not
/// the same kind of thing and are not handled alike: a message is delivered once and stamped so
/// it is not handed over again, while a poll stays outstanding until the agent answers it or
/// the window closes. Flattening them would put that difference behind a type test.
/// </para>
/// </remarks>
public sealed record Inbox(
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ClosurePoll> ClosurePolls)
{
    /// <summary>
    /// Gets a value indicating whether there is anything here to act on.
    /// </summary>
    /// <remarks>
    /// Named for what it means rather than derived from one collection. A property reading only
    /// <see cref="Messages"/> would have quietly under-reported the moment polls arrived, which
    /// is the failure <see cref="ReapResult"/> records having avoided.
    /// </remarks>
    public bool IsEmpty => Messages.Count == 0 && ClosurePolls.Count == 0;
}
