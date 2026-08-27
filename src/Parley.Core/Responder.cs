namespace Parley.Core;

/// <summary>
/// What a message is attributed to, per SPEC.md §11.3.
/// </summary>
/// <remarks>
/// In <see cref="Parley.Core"/> because both sides of the wire enforce the same rule and neither
/// can reference the other. The broker refuses a reserved claim on every send; an adapter refuses
/// one at launch, so a server configured this way fails visibly rather than failing every
/// operation afterwards. Two copies of a protocol constant is how the two come to disagree, and
/// the disagreement would be silent in the direction that matters.
/// </remarks>
public static class Responder
{
    /// <summary>
    /// The namespace SPEC.md §11.3 reserves for the broker's own attributions.
    /// </summary>
    /// <remarks>
    /// An adapter claiming it could forge a broker-authored close — a ghost, a forced conclusion
    /// — which is the one class of message an agent is entitled to believe came from the
    /// protocol rather than from a peer.
    /// </remarks>
    public const string ReservedPrefix = "parley:";

    /// <summary>
    /// Whether an attribution claims the broker's reserved namespace.
    /// </summary>
    /// <param name="responderModel">The attribution.</param>
    /// <returns><see langword="true"/> if it must be refused.</returns>
    /// <remarks>
    /// Case-insensitive, because the point is what a reader would take the token to mean rather
    /// than what it literally is — and a refusal that <c>Parley:</c> slipped past would be a
    /// refusal in name only.
    /// </remarks>
    public static bool IsReserved(string? responderModel) =>
        responderModel is not null
        && responderModel.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);
}
