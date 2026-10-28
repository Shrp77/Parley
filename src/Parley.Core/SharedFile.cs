namespace Parley.Core;

/// <summary>
/// A file in a conversation's folder of the file location (SPEC.md §9.5).
/// </summary>
/// <param name="ConversationId">The conversation it belongs to.</param>
/// <param name="Folder">The conversation's folder name on disk.</param>
/// <param name="Name">Its name.</param>
/// <param name="SizeBytes">How many bytes it is.</param>
/// <param name="Sha256">The broker's hash of its bytes.</param>
/// <param name="PlacedBy">The agent that placed it, or <see langword="null"/> when the person placed it.</param>
/// <param name="PlacedUtc">When it was placed; a placed file never changes.</param>
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
    DateTimeOffset PlacedUtc);

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
/// <param name="Placements">How many files it has placed.</param>
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
/// <param name="NotListed">
/// Files in the folders listed that nothing placed, copied or renamed there by hand, and so are in no list (SPEC.md
/// §9.5). A count and never the names, which are text nothing has checked.
/// </param>
public sealed record SharedFileListing(SharedFilePage Page, IReadOnlyList<FilePlacementCount> Placements, int NotListed = 0)
{
    /// <summary>The sentence a surface shows when <see cref="NotListed"/> is more than zero.</summary>
    /// <remarks>
    /// One sentence for every surface, so the terminal, the web UI and the command line say the same thing about the
    /// same files. It says why they are not listed rather than naming them, and what to do.
    /// </remarks>
    public string? NotListedSentence => NotListedSentenceFor(NotListed);

    /// <summary>The sentence for a count of files not listed.</summary>
    /// <param name="notListed">The count.</param>
    /// <returns>The sentence, or <see langword="null"/> when the count is zero.</returns>
    public static string? NotListedSentenceFor(int notListed) => notListed <= 0
        ? null
        : $"{notListed} file(s) in these folders are not listed, because nothing placed them: something other than " +
          "Parley put them there, and the sweep this list started deletes them. Place a file through Parley to share it.";
}

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
