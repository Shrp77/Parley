namespace Parley.Core;

/// <summary>What became of an entry in the file location that a sweep could not leave as it was (SPEC.md §9.5).</summary>
public enum FileProblemKind
{
    /// <summary>A placed file whose bytes no longer hash to what was placed, moved to quarantine.</summary>
    Quarantined,

    /// <summary>An entry the sweep could not read, move or delete because another program held it.</summary>
    Locked,
}

/// <summary>A problem with the file location, shown to the person until they dismiss it (SPEC.md §9.5, §10.2).</summary>
/// <param name="Id">The problem's identifier, never another's.</param>
/// <param name="Kind">What happened.</param>
/// <param name="ConversationId">The conversation whose folder held it, or <see langword="null"/> for none.</param>
/// <param name="Path">The entry, as a path inside the location with <c>/</c> between its parts.</param>
/// <param name="PlacedSha256">For a quarantined file, the hash placed.</param>
/// <param name="FoundSha256">For a quarantined file, the hash found.</param>
/// <param name="MovedTo">For a quarantined file, where in the quarantine directory it now is.</param>
/// <param name="Detail">The broker's own sentence about it.</param>
/// <param name="RaisedUtc">When it was raised.</param>
/// <param name="DismissedUtc">When the person dismissed it, or <see langword="null"/>.</param>
/// <remarks>
/// The path is text nothing checked when the entry was put there by hand, so a surface draws it as somebody else's
/// text. Kept rather than logged because the person may not be watching when a sweep runs.
/// </remarks>
public sealed record FileProblem(
    long Id,
    FileProblemKind Kind,
    string? ConversationId,
    string Path,
    ContentHash? PlacedSha256,
    ContentHash? FoundSha256,
    string? MovedTo,
    string Detail,
    DateTimeOffset RaisedUtc,
    DateTimeOffset? DismissedUtc);

/// <summary>The problems with the file location, and where the quarantine directory is.</summary>
/// <param name="Problems">The problems, those waiting first and each oldest first.</param>
/// <param name="Quarantine">The quarantine directory, on the broker's machine.</param>
public sealed record FileProblemsReport(IReadOnlyList<FileProblem> Problems, FileLocation Quarantine)
{
    /// <summary>Gets how many the person has not dismissed.</summary>
    public int Waiting => Problems.Count(p => p.DismissedUtc is null);
}
