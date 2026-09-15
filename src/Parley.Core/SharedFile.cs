namespace Parley.Core;

/// <summary>
/// A file in a conversation's folder of the file location (SPEC.md §9.5).
/// </summary>
/// <param name="ConversationId">The conversation it belongs to.</param>
/// <param name="Folder">The conversation's folder name on disk.</param>
/// <param name="Name">Its name.</param>
/// <param name="SizeBytes">How many bytes its current content is.</param>
/// <param name="Sha256">The broker's hash of its current content.</param>
/// <param name="PlacedBy">The agent that placed it, or <see langword="null"/> when the person put it there.</param>
/// <param name="PlacedUtc">When it was first placed.</param>
/// <param name="ChangedUtc">When its current content arrived, by any route.</param>
/// <remarks>
/// No contents, ever. Nothing in Parley reads a file's bytes except to hash and copy them, and a type that
/// could carry them would be a type something eventually puts in a reply.
/// </remarks>
public sealed record SharedFile(
    string ConversationId,
    string Folder,
    SharedFileName Name,
    long SizeBytes,
    ContentHash Sha256,
    AgentName? PlacedBy,
    DateTimeOffset PlacedUtc,
    DateTimeOffset ChangedUtc);

/// <summary>
/// A conversation's share of the file location.
/// </summary>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Files">How many files it holds.</param>
/// <param name="Bytes">How many bytes they come to.</param>
public sealed record ConversationFilesTotal(string ConversationId, int Files, long Bytes);

/// <summary>
/// How many placements, and how many bytes, one agent has made (SPEC.md §9.5).
/// </summary>
/// <param name="Agent">The agent, which may since have been purged.</param>
/// <param name="Placements">Placements, and replacements with new content.</param>
/// <param name="Bytes">The bytes those carried.</param>
/// <param name="LastUtc">When the most recent one was made.</param>
/// <remarks>
/// A string rather than an <see cref="AgentName"/>, because a count outlives its agent and a name that no longer
/// resolves is still the right thing to print.
/// </remarks>
public sealed record FilePlacementCount(string Agent, long Placements, long Bytes, DateTimeOffset LastUtc);

/// <summary>
/// One page of the file location.
/// </summary>
/// <param name="Files">The files on this page, ordered by conversation and then by name.</param>
/// <param name="Totals">The conversations on this page, each counted whole.</param>
/// <param name="TotalFiles">Every file listed across all pages.</param>
/// <param name="TotalBytes">The bytes those come to.</param>
/// <param name="NextPageToken">What to ask with for the next page, or <see langword="null"/> at the end.</param>
public sealed record SharedFilePage(
    IReadOnlyList<SharedFile> Files,
    IReadOnlyList<ConversationFilesTotal> Totals,
    int TotalFiles,
    long TotalBytes,
    string? NextPageToken);

/// <summary>
/// A page of the file location as the person sees it: the files, and the placement counts only the person is given.
/// </summary>
/// <param name="Page">The page.</param>
/// <param name="Placements">How many placements and bytes each agent has made.</param>
public sealed record SharedFileListing(SharedFilePage Page, IReadOnlyList<FilePlacementCount> Placements);

/// <summary>
/// Where a file or folder is, or why there is no such thing to point at.
/// </summary>
/// <param name="Location">The machine and path, when there is one.</param>
/// <param name="Refusal">Why not: no such file, a conversation with no files, or an ambiguous name.</param>
public sealed record FileLocationAnswer(FileLocation? Location, FileRefusal? Refusal);

/// <summary>
/// Where a file or folder is, as the broker's machine sees it.
/// </summary>
/// <param name="Machine">The broker's machine name (SPEC.md §4.1.1).</param>
/// <param name="Path">The absolute path there.</param>
public sealed record FileLocation(string Machine, string Path);
