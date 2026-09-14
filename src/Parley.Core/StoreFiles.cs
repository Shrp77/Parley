namespace Parley.Core;

/// <summary>
/// The files that sit beside a store and belong to it, by suffix.
/// </summary>
/// <remarks>
/// <para>
/// Names rather than paths, and no I/O. The broker takes these and <c>project remove --destroy</c>
/// deletes them, and those two live in assemblies that must never reference each other —
/// <c>Parley.Setup</c> has no route to a store and <c>GraphTests</c> holds it to that. A suffix both
/// spell is the one thing they have to agree about, so it is spelled once, here.
/// </para>
/// <para>
/// SQLite's own companions (<c>-wal</c>, <c>-shm</c>, <c>-journal</c>) are not here because Parley
/// does not choose them.
/// </para>
/// </remarks>
public static class StoreFiles
{
    /// <summary>
    /// Held exclusively by the one broker serving the store, for as long as it serves it.
    /// </summary>
    /// <remarks>
    /// Never deleted when a broker stops, only by destroying the project. On Linux an exclusive
    /// lock belongs to an open file rather than to a name, so a broker unlinking the file on its
    /// way out would let the next two starters each create and lock a different file of the same
    /// name — two owners, each holding a lock that says it is the only one.
    /// </remarks>
    public const string Lock = ".lock";

    /// <summary>
    /// Which process holds the lock, written by that process for the benefit of the one refused.
    /// </summary>
    /// <remarks>
    /// A separate file because the lock itself cannot be read by anybody else on Windows, and a
    /// refusal that cannot say who holds the store sends somebody looking through every process on
    /// the machine — which is exactly what the owner had to do to find a terminal tab.
    /// </remarks>
    public const string Owner = ".owner";
}
