namespace Parley.Core;

/// <summary>
/// The volume Notify actually carries, for SPEC.md §17 question 1.
/// </summary>
/// <param name="Flows">One entry per sending pair, busiest first.</param>
/// <remarks>
/// <para>
/// The question records that nothing bounds Notify and that the answer is to measure rather than
/// guess a limit. Two numbers decide it — how many and how big — and this keeps both past the
/// retention that removes the messages themselves.
/// </para>
/// <para>
/// A rollup rather than a row per message, unlike the referral record. A referral names a third
/// agent and a counter cannot hold which; a notification's content is prose that is pruned with
/// the message and should be, so nothing a decision needs is lost by counting.
/// </para>
/// </remarks>
public sealed record NotifyVolumeReport(IReadOnlyList<NotifyFlow> Flows)
{
    /// <summary>Gets whether anything has been notified at all.</summary>
    public bool IsEmpty => Flows is null || Flows.Count == 0;

    /// <summary>Gets every notification sent, across all flows.</summary>
    public long Sent => Flows?.Sum(f => f.Sent) ?? 0;

    /// <summary>Gets the mean size of a notification across all flows.</summary>
    /// <remarks>
    /// Weighted by the messages rather than averaged across the flows, so one pair sending a
    /// thousand short notices is not balanced out by a pair that sent one long one.
    /// </remarks>
    public long Mean
    {
        get
        {
            var sent = Sent;
            return sent == 0 ? 0 : (Flows?.Sum(f => f.Characters) ?? 0) / sent;
        }
    }

    /// <summary>
    /// How many notifications each recipient has received in total.
    /// </summary>
    /// <remarks>
    /// The number a survey threshold reads. Asking an agent whether notifications are worth
    /// having is only meaningful once it has had enough to judge, and the count that matters to
    /// it is everything it received rather than what any one peer sent.
    /// </remarks>
    public IReadOnlyList<(string Agent, long Received)> Received =>
        (Flows ?? [])
            .GroupBy(f => f.To, StringComparer.Ordinal)
            .Select(g => (Agent: g.Key, Received: g.Sum(f => f.Sent)))
            .OrderByDescending(x => x.Received)
            .ThenBy(x => x.Agent, StringComparer.Ordinal)
            .ToArray();
}
