using System.Globalization;
using Parley.Core;

namespace Parley.Client.Files;

/// <summary>
/// What the person's placement came to, in words (SPEC.md §10.2 <c>place_persons_file</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One sentence for the command line, the terminal and the web UI</b>, so the three cannot disagree about what a
/// refusal meant. Each surface still makes it safe to draw in its own way: a file's name and the broker's detail are
/// somebody's text, and this does not neutralise them.
/// </para>
/// <para>
/// <b>A renamed placement says so.</b> The broker turns characters outside the set into <c>_</c> (§9.5), and a person
/// who is not told looks for the file under the name they typed.
/// </para>
/// </remarks>
public static class PlacementSaid
{
    /// <summary>The sentence for one placement.</summary>
    /// <param name="placement">What the broker said.</param>
    /// <param name="asked">The name the person asked for.</param>
    /// <param name="size">How this surface writes a size.</param>
    /// <returns>The sentence, not yet safe to draw.</returns>
    public static string Of(PersonPlacement placement, string asked, Func<long, string> size)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(size);

        var renamed = placement.InPlace && !string.Equals(placement.Name, asked, StringComparison.Ordinal)
            ? $" It was asked for as '{asked}': characters other than letters, digits, '.', '_' and '-' became '_'."
            : string.Empty;

        return placement.Outcome switch
        {
            PlaceOutcome.Placed when placement.InPlace => string.Create(
                CultureInfo.InvariantCulture,
                $"Placed {placement.Name} in {placement.ConversationId}: {size(placement.SizeBytes)}, sha256 " +
                $"{placement.Sha256?.Short ?? "unknown"}.{renamed}"),
            PlaceOutcome.AlreadyCurrent when placement.InPlace =>
                $"{placement.Name} is already in {placement.ConversationId} with exactly this content. Nothing was written.{renamed}",
            _ => $"Not placed: {placement.Detail ?? Refused(placement.Rejection)}",
        };
    }

    /// <summary>A refusal the broker gave no words for.</summary>
    /// <param name="rejection">The reason.</param>
    /// <returns>The words.</returns>
    private static string Refused(FileRejection rejection) => rejection switch
    {
        FileRejection.NameHoldsOtherContent =>
            "that name already holds other content. A placed file is never changed: place the new version under a new name.",
        FileRejection.NamePlacedByAnother => "an agent placed that name, so it is theirs.",
        FileRejection.ConversationNotAssignable => "there is no such conversation.",
        FileRejection.ExtensionNotAllowed => "only a .md or .txt file can be placed.",
        _ => rejection.ToString() + ".",
    };
}
