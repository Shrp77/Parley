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
    /// the machine.
    /// </remarks>
    public const string Owner = ".owner";

    /// <summary>
    /// The file location: a directory holding one folder per conversation that has files (SPEC.md §9.5).
    /// </summary>
    /// <remarks>
    /// A directory rather than a file, and beside the store rather than inside a user's profile, so that
    /// destroying a project takes its files with it and a person looking for them finds them next to the
    /// store they already know.
    /// </remarks>
    public const string Files = ".files";

    /// <summary>
    /// Where placements in progress are written before they are moved into the location.
    /// </summary>
    /// <remarks>
    /// Beside the location and never inside it: the folder a person opens and the
    /// watcher watches then never holds a half-uploaded file, and on the same disk a finished upload still
    /// moves into place in one step. Emptied when a broker starts, because nothing survives a broker that
    /// could have finished writing it.
    /// </remarks>
    public const string FilesIncoming = ".files-incoming";

    /// <summary>
    /// Begins the name of a copy of the store taken before it was migrated, followed by the schema reached and the time.
    /// </summary>
    /// <remarks>
    /// A prefix, because there is one copy per upgrade. Destroying a project deletes every one: each holds the text of
    /// every conversation up to its upgrade, and a destroy that left them would not be what it says it is.
    /// </remarks>
    public const string BeforeSchema = ".before-schema-";
}
