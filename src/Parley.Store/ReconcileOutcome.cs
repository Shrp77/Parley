namespace Parley.Store;

/// <summary>What bringing a file's row into line with a measurement of it did.</summary>
/// <remarks>
/// Three outcomes rather than a flag, because "nothing written" means two things a caller must tell apart. A measurement
/// that matched the row needs nothing more; one dropped because the row moved on since it was read may be the newer of
/// two, and the file must be looked at again or the older hash stays recorded.
/// </remarks>
public enum ReconcileOutcome
{
    /// <summary>The content was what the row records; at most its time was brought up to date.</summary>
    Unchanged = 0,

    /// <summary>The content had changed: the row now records it, and fetchers' notices were raised.</summary>
    Changed = 1,

    /// <summary>
    /// The row's hash or time moved on after it was read, so nothing was written. The file should be measured again.
    /// </summary>
    MovedOn = 2,

    /// <summary>The row is gone.</summary>
    Gone = 3,
}
