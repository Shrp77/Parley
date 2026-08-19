namespace Parley.Store;

/// <summary>
/// What one reaping sweep did.
/// </summary>
/// <param name="Orphaned">
/// Exchanges moved to the orphaned state, releasing their pair slots.
/// </param>
/// <param name="Contended">
/// Expired exchanges that another writer changed between the scan and the write, and which
/// were therefore left alone.
/// </param>
/// <remarks>
/// <para>
/// Reported rather than logged so a caller can decide what is worth saying. A sweep that
/// orphans nothing is the normal case and should not produce noise; a sweep that orphans
/// dozens is worth noticing, and one that is persistently contended is worth investigating.
/// </para>
/// <para>
/// There is deliberately no summary property here. A <c>DidWork</c> derived from
/// <see cref="Orphaned"/> would quietly under-report the moment this record gains a second
/// count — retention pruning is coming — and nothing would fail to say so. A caller that
/// wants "did anything happen" names the fields it means.
/// </para>
/// <para>
/// <see cref="Contended"/> is not an error count. An exchange that changed underneath the
/// sweep almost always changed because it was answered, which is the outcome the whole
/// system prefers — the deadline simply lost a race with the agent, and the next sweep will
/// find nothing to do.
/// </para>
/// </remarks>
public sealed record ReapResult(int Orphaned, int Contended)
{
    /// <summary>
    /// An empty result, for a sweep that found nothing expired.
    /// </summary>
    public static ReapResult Nothing { get; } = new(0, 0);
}
