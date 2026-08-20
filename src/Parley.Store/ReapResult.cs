namespace Parley.Store;

/// <summary>
/// What one reaping sweep did.
/// </summary>
/// <param name="Orphaned">
/// Exchanges moved to the orphaned state, releasing their wait-for edges.
/// </param>
/// <param name="Ghosted">
/// Orphans closed because their grace period elapsed and nobody returned.
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
/// <see cref="Orphaned"/> would have under-reported the moment this record gained a second
/// count, which is exactly what <see cref="Ghosted"/> is, and nothing would have failed to
/// say so. A caller that wants "did anything happen" names the fields it means.
/// </para>
/// <para>
/// <see cref="Contended"/> is not an error count. An exchange that changed underneath the
/// sweep almost always changed because it was answered, which is the outcome the whole
/// system prefers — the deadline simply lost a race with the agent, and the next sweep will
/// find nothing to do.
/// </para>
/// </remarks>
public sealed record ReapResult(int Orphaned, int Ghosted, int Contended)
{
    /// <summary>
    /// An empty result, for a sweep that found nothing to do.
    /// </summary>
    public static ReapResult Nothing { get; } = new(0, 0, 0);
}
