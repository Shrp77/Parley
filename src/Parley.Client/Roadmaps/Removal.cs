using System.Globalization;
using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// What an edit that removes items would leave in flight, said before it is saved (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Removing an item removes nothing it caused.</b> Its conversation stays and any request in it stays open, so the
/// agent holding it goes on working on something that is no longer on the roadmap, and is not told. That is right, since
/// only the person closes work, but it is not what somebody deleting a line expects, so it is described first.
/// </para>
/// <para>
/// <b>One derivation for both surfaces</b>, so the terminal and the web console warn about the same items in the same
/// words. Read from the facts the roadmap read already carries, so warning costs no further call.
/// </para>
/// </remarks>
public static class Removal
{
    /// <summary>The live items an edit would remove whose conversation still has an open exchange.</summary>
    /// <param name="roadmap">The roadmap as read.</param>
    /// <param name="final">What the person wants it to become.</param>
    /// <returns>Those items, in the roadmap's order; empty when nothing in flight is affected.</returns>
    public static IReadOnlyList<RoadmapItem> StillOpen(Roadmap roadmap, RoadmapContent final)
    {
        ArgumentNullException.ThrowIfNull(roadmap);
        ArgumentNullException.ThrowIfNull(final);

        var kept = final.Items.Select(i => i.Number).OfType<int>().ToHashSet();

        return [.. roadmap.Items
            .Where(i => !i.Removed && !kept.Contains(i.Number) && i.Facts is { OpenExchanges: > 0 })
            .OrderBy(i => i.Position)];
    }

    /// <summary>Whether a person agreed to every item an edit would leave in flight.</summary>
    /// <param name="open">What <see cref="StillOpen"/> found for the content being saved now.</param>
    /// <param name="agreed">The item numbers the person was shown and went ahead with, as <see cref="Numbers"/> wrote them.</param>
    /// <returns><see langword="true"/> when nothing is open or every open item was among those shown.</returns>
    /// <remarks>
    /// Going ahead is agreement to what was described, so an open item taken out after the description, or one whose work
    /// opened since, is described again. Anything in <paramref name="agreed"/> that is not a number agrees to nothing.
    /// </remarks>
    public static bool Covers(IReadOnlyList<RoadmapItem> open, string? agreed)
    {
        ArgumentNullException.ThrowIfNull(open);

        var shown = (agreed ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .Where(n => n > 0)
            .ToHashSet();

        return open.All(i => shown.Contains(i.Number));
    }

    /// <summary>The numbers of the items described, for a form to carry back with the person's choice.</summary>
    /// <param name="open">What <see cref="StillOpen"/> found.</param>
    /// <returns>The numbers, comma-separated.</returns>
    public static string Numbers(IReadOnlyList<RoadmapItem> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        return string.Join(",", open.Select(i => i.Number.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The sentence a surface shows before saving such an edit.</summary>
    /// <param name="items">What <see cref="StillOpen"/> found.</param>
    /// <returns>The description, or <see langword="null"/> when there is nothing to say.</returns>
    public static string? Describe(IReadOnlyList<RoadmapItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return null;
        }

        var named = string.Join(
            "; ",
            items.Select(i => string.Create(CultureInfo.InvariantCulture, $"item {i.Number}, \"{i.Text}\", in {i.ConversationName}")));

        return (items.Count == 1 ? "This removes an item whose work is still open: " : "This removes items whose work is still open: ")
            + named
            + ". The conversation and its open requests stay, and the agents holding them are not told. "
            + "Close the work in the conversation if it should stop.";
    }
}
