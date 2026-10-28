namespace Parley.Core;

/// <summary>
/// The bounds on what crosses about files in one call (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// Constitution Principle IV: what an operation returns about files is text that crosses, and a list or a batch
/// has a maximum count per call. The page bounds are the ones the operator's <c>conversations</c>
/// already use, so there is one rule for every list in Parley.
/// </para>
/// <para>
/// The broker applies these whatever a caller asks for. A bound that trusted the caller's number would bound
/// only the callers that meant well.
/// </para>
/// </remarks>
public static class FileLimits
{
    /// <summary>Files on a page when the caller names no size.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The most files on a page, whatever is asked for.</summary>
    public const int MaximumPageSize = 200;

    /// <summary>The most gone notices one inbox collection hands over.</summary>
    public const int NoticesPerCollection = 200;

    /// <summary>The most conversations an ambiguity refusal names.</summary>
    public const int ConversationsPerRefusal = 200;

    /// <summary>The most names one removal may carry.</summary>
    public const int NamesPerRemoval = 200;

    /// <summary>The bytes in one piece of a streamed placement or fetch.</summary>
    /// <remarks>
    /// Well under gRPC's default message limit of 4 MiB, which is left alone: raising it would have the broker
    /// allocate whole files per call.
    /// </remarks>
    public const int ChunkBytes = 256 * 1024;

    /// <summary>A page size as the broker applies it.</summary>
    /// <param name="asked">What the caller asked for; zero or less means the default.</param>
    /// <returns>The size used.</returns>
    public static int PageSize(int asked) =>
        asked <= 0 ? DefaultPageSize : Math.Min(asked, MaximumPageSize);
}

/// <summary>
/// Why a file operation did not do what was asked.
/// </summary>
public enum FileRejection
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>The name breaks a rule other than its extension.</summary>
    BadName,

    /// <summary>Another agent, or the person, placed that name, so the caller may not place over or remove it.</summary>
    NamePlacedByAnother,

    /// <summary>The name holds other content already; a new version goes under a new name (SPEC.md §9.5).</summary>
    NameHoldsOtherContent,

    /// <summary>No file has that name.</summary>
    NoSuchFile,

    /// <summary>Several conversations hold the name, and none was named; nothing was done.</summary>
    AmbiguousName,

    /// <summary>The caller may not place into that conversation, or must name one.</summary>
    ConversationNotAssignable,

    /// <summary>The conversation is closed.</summary>
    ConversationClosed,

    /// <summary>
    /// The content is not the one expected, or the file could not be read for the broker: held for writing by another
    /// program, or not readable by the broker at all. Bytes changed on disk are <see cref="Tampered"/>. The refusal's
    /// detail says which (SPEC.md §9.5).
    /// </summary>
    Changed,

    /// <summary>The broker's disk has no room.</summary>
    NoSpace,

    /// <summary>The name does not end in <c>.md</c> or <c>.txt</c>.</summary>
    ExtensionNotAllowed,

    /// <summary>A reported fetch is not one the broker streamed to that agent with that content.</summary>
    NotFetched,

    /// <summary>A removal named more files than one call may carry.</summary>
    TooManyNames,

    /// <summary>
    /// A placement's name held characters outside the set, and the spelling it would be stored under is already
    /// another file's name in that conversation; nothing was placed.
    /// </summary>
    SafeNameTaken,

    /// <summary>
    /// The bytes on disk are not what was placed: something other than Parley changed them, so the file is no longer
    /// reliable, and the broker has started a sweep that sets it aside (SPEC.md §9.5).
    /// </summary>
    Tampered,
}

/// <summary>
/// What placing a file came to.
/// </summary>
public enum PlaceOutcome
{
    /// <summary>A new name, now in the location.</summary>
    Placed,

    /// <summary>The caller's own file already held exactly this content; nothing was written or counted.</summary>
    AlreadyCurrent,

    /// <summary>Another agent, or the person, placed that name; nothing changed.</summary>
    PlacedByAnother,

    /// <summary>
    /// The caller's own file holds other content under that name; nothing changed. A placed file is never changed, so a
    /// new version goes under a new name (SPEC.md §9.5).
    /// </summary>
    HoldsOtherContent,

    /// <summary>The name is too long for the conversation's folder; nothing was placed.</summary>
    DoesNotFit,
}

/// <summary>
/// A file named by conversation and name, or by name alone.
/// </summary>
/// <param name="ConversationId">The conversation, or <see langword="null"/> to look across the location.</param>
/// <param name="Name">The file's name, as the caller wrote it.</param>
public sealed record FileRef(string? ConversationId, string Name);

/// <summary>
/// One file an operation refused, and why.
/// </summary>
/// <param name="File">The file as it was named.</param>
/// <param name="Rejection">Why.</param>
/// <param name="PlacedBy">The placer, when the refusal is that another agent placed it.</param>
/// <param name="Conversations">When the name is ambiguous, at most <see cref="FileLimits.ConversationsPerRefusal"/> conversations holding it.</param>
/// <param name="ConversationsMore">How many more conversations hold it than are listed.</param>
/// <param name="Detail">The rule or the reason in words, when the broker gave them.</param>
public sealed record FileRefusal(
    FileRef File,
    FileRejection Rejection,
    string? PlacedBy,
    IReadOnlyList<string> Conversations,
    int ConversationsMore,
    string? Detail = null)
{
    /// <summary>A refusal that names nothing beyond its reason.</summary>
    /// <param name="file">The file.</param>
    /// <param name="rejection">Why.</param>
    /// <returns>The refusal.</returns>
    public static FileRefusal Of(FileRef file, FileRejection rejection) => new(file, rejection, null, [], 0);
}

/// <summary>
/// What the person's placement came to.
/// </summary>
/// <param name="Outcome">Placed or already current; anything else was refused, and nothing was written.</param>
/// <param name="Rejection">Why it was refused, or <see cref="FileRejection.None"/>.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Name">The name placed, which characters outside the set may have changed from the one asked for.</param>
/// <param name="SizeBytes">How many bytes were placed.</param>
/// <param name="Sha256">The broker's hash of them, or <see langword="null"/> when refused.</param>
/// <param name="Detail">The rule in words, on a refusal.</param>
public sealed record PersonPlacement(
    PlaceOutcome? Outcome,
    FileRejection Rejection,
    string ConversationId,
    string Name,
    long SizeBytes,
    ContentHash? Sha256,
    string? Detail)
{
    /// <summary>Gets a value indicating whether the file is in the location now, as placed or already current.</summary>
    public bool InPlace => Rejection == FileRejection.None;
}

/// <summary>
/// What a removal did.
/// </summary>
/// <param name="Removed">The files removed.</param>
/// <param name="Refused">The names refused, each with its reason.</param>
/// <param name="BytesFreed">The bytes the removed files held.</param>
/// <param name="Rejection">
/// <see cref="FileRejection.TooManyNames"/> when the request was refused whole and nothing was removed;
/// otherwise <see cref="FileRejection.None"/>.
/// </param>
public sealed record RemoveFilesOutcome(
    IReadOnlyList<FileRef> Removed,
    IReadOnlyList<FileRefusal> Refused,
    long BytesFreed,
    FileRejection Rejection);

/// <summary>
/// What a prune of the file location did.
/// </summary>
/// <param name="FilesRemoved">How many files went.</param>
/// <param name="BytesFreed">The bytes they held.</param>
public sealed record PruneFilesOutcome(int FilesRemoved, long BytesFreed);
