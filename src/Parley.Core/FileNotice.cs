namespace Parley.Core;

/// <summary>
/// That a file an agent fetched has gone since (SPEC.md §9.5).
/// </summary>
/// <param name="ConversationId">The conversation the file was in.</param>
/// <param name="Name">The file's name.</param>
/// <param name="Had">The hash of the content the agent fetched.</param>
/// <param name="GoneUtc">When it went.</param>
/// <remarks>
/// Not a message (§5): it opens no exchange and nothing is owed back. Only a file's going is noticed, because a placed
/// file never changes: a new version is a new name.
/// </remarks>
public sealed record FileNotice(string ConversationId, string Name, ContentHash Had, DateTimeOffset GoneUtc);

/// <summary>
/// The gone notices one collection hands over.
/// </summary>
/// <param name="Notices">At most <see cref="FileLimits.NoticesPerCollection"/>, oldest first.</param>
/// <param name="StillWaiting">How many are left for a later collection.</param>
public sealed record FileNoticeBatch(IReadOnlyList<FileNotice> Notices, int StillWaiting)
{
    /// <summary>A collection with nothing in it.</summary>
    public static FileNoticeBatch None { get; } = new([], 0);
}
