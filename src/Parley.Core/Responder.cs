namespace Parley.Core;

/// <summary>
/// What a message is attributed to (SPEC.md §11.3).
/// </summary>
/// <remarks>
/// In <see cref="Parley.Core"/> because both sides of the wire enforce the same rule and neither can
/// reference the other. The broker refuses a reserved claim on every send; an adapter refuses one at
/// launch, so a server configured this way fails visibly rather than failing every operation after.
/// </remarks>
public static class Responder
{
    /// <summary>The namespace §11.3 reserves for the broker's own attributions.</summary>
    /// <remarks>
    /// An adapter claiming it could forge a close the person never made: work that looks deliberately
    /// ended when it was abandoned, or ended by a person when an agent ended it.
    /// </remarks>
    public const string ReservedPrefix = "parley:";

    /// <summary>The attribution the broker records on the <c>closed</c> Results it writes.</summary>
    public const string Broker = "parley:broker";

    /// <summary>Whether an attribution claims the broker's reserved namespace.</summary>
    /// <param name="responderModel">The attribution.</param>
    /// <returns><see langword="true"/> if it must be refused.</returns>
    /// <remarks>
    /// Case-insensitive, because the point is what a reader would take the token to mean — and a
    /// refusal that <c>Parley:</c> slipped past would be a refusal in name only.
    /// </remarks>
    public static bool IsReserved(string? responderModel) =>
        responderModel is not null
        && responderModel.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);
}
