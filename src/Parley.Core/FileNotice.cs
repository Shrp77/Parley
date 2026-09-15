namespace Parley.Core;

/// <summary>
/// That a file an agent fetched has changed or gone since (SPEC.md §9.5).
/// </summary>
/// <param name="ConversationId">The conversation the file was in.</param>
/// <param name="Name">The file's name.</param>
/// <param name="Had">The hash of the content the agent fetched.</param>
/// <param name="Now">The hash of the content there now, or <see langword="null"/> when the file is gone.</param>
/// <param name="SizeBytes">The size now, or <see langword="null"/> when the file is gone.</param>
/// <param name="ChangedUtc">When it last changed or went.</param>
/// <remarks>
/// Not a message (§5): it opens no exchange and nothing is owed back. A name and two hashes, bounded like
/// everything else that crosses, and never the content itself.
/// </remarks>
public sealed record FileNotice(
    string ConversationId,
    string Name,
    ContentHash Had,
    ContentHash? Now,
    long? SizeBytes,
    DateTimeOffset ChangedUtc)
{
    /// <summary>Whether the file is gone rather than changed.</summary>
    public bool Gone => Now is null;
}

/// <summary>
/// The change notices one collection hands over.
/// </summary>
/// <param name="Notices">At most <see cref="FileLimits.NoticesPerCollection"/>, oldest change first.</param>
/// <param name="StillWaiting">How many are left for a later collection.</param>
public sealed record FileNoticeBatch(IReadOnlyList<FileNotice> Notices, int StillWaiting)
{
    /// <summary>A collection with nothing in it.</summary>
    public static FileNoticeBatch None { get; } = new([], 0);
}
