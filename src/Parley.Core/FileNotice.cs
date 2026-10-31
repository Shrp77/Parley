namespace Parley.Core;

/// <summary>
/// That a file an agent fetched or placed has gone since, or was tampered with (SPEC.md §9.5).
/// </summary>
/// <param name="ConversationId">The conversation the file was in.</param>
/// <param name="Name">The file's name.</param>
/// <param name="Had">The hash of the content the agent fetched or placed.</param>
/// <param name="GoneUtc">When it went.</param>
/// <param name="Found">
/// For a file that was tampered with, the hash of the bytes found where it should have been; <see langword="null"/>
/// for a file that is only gone.
/// </param>
/// <remarks>
/// Not a message (§5): it opens no exchange and nothing is owed back. A placed file never changes through Parley: a
/// new version is a new name, so bytes that changed were changed by something else, and the file is no longer
/// reliable. It goes either way; the notice says which.
/// </remarks>
public sealed record FileNotice(string ConversationId, string Name, ContentHash Had, DateTimeOffset GoneUtc, ContentHash? Found = null)
{
    /// <summary>Gets a value indicating whether the file went because it was tampered with.</summary>
    public bool Tampered => Found is not null;
}

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
