using System.Globalization;
using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// The words a surface uses for a roadmap and for where each of its items stands.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because the states are derived and the sentences should not be.</b> <see cref="ItemStates"/> works out
/// where an item stands from what the broker holds; this says it. A surface that worded them itself would word the same
/// state two ways the first time one was edited.
/// </para>
/// <para>
/// A state that could not be read is said to be that, and never as <i>not started</i> (SPEC.md §5.7).
/// </para>
/// </remarks>
public static class ItemWording
{
    /// <summary>One roadmap as a row in a list.</summary>
    /// <param name="roadmap">The roadmap's summary.</param>
    /// <returns>For example <c>#2 Billing export — 5 items, 1 started</c>.</returns>
    public static string Row(RoadmapSummary roadmap)
    {
        ArgumentNullException.ThrowIfNull(roadmap);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"#{roadmap.Number} {roadmap.Title} — {roadmap.LiveItems} item{(roadmap.LiveItems == 1 ? string.Empty : "s")}, {roadmap.StartedItems} started");
    }

    /// <summary>Where one item stands, in words.</summary>
    /// <param name="state">The item's derived state.</param>
    /// <returns>The words, which may name an agent and a blocker the agent gave.</returns>
    public static string Standing(ItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var holders = string.Join(", ", state.Holders.Select(h => h.Role));

        var said = state.Standing switch
        {
            ItemStanding.NotStarted => "not started",
            ItemStanding.InProgress => holders.Length > 0 ? $"in progress with {holders}" : "in progress",
            ItemStanding.WaitingOnAgent => holders.Length > 0 ? $"waiting on {holders}" : "waiting on an agent",
            ItemStanding.WaitingOnPerson => "waiting on you",
            ItemStanding.Blocked => state.Blocker is { Length: > 0 } blocker ? $"blocked: {blocker}" : "blocked",
            ItemStanding.Done => "done",
            ItemStanding.Closed => "closed",
            _ => "could not be read",
        };

        return state.TextChanged ? said + " (reworded since it began)" : said;
    }

    /// <summary>Which roadmap item a conversation is the work on, for a list of conversations (SPEC.md §5.7).</summary>
    /// <param name="item">The broker's record that the conversation is an item's.</param>
    /// <returns>For example <c>item 3 of #1 Billing export</c>, or <c>formerly item 3 of #1 Billing export</c>.</returns>
    /// <remarks>
    /// Taken from the reservation and never from the conversation's name or messages, so an agent cannot make a
    /// conversation look like an item's. A removed item's conversation stays, belongs to no item, and says which it was.
    /// </remarks>
    public static string Belongs(RoadmapItemRef item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(item.Removed ? "formerly " : string.Empty)}item {item.ItemNumber} of #{item.RoadmapNumber} {item.Title}");
    }

    /// <summary>What comes next in a roadmap.</summary>
    /// <param name="standing">The roadmap's derived standing.</param>
    /// <returns>A sentence: the next item, or why there is none.</returns>
    public static string Next(RoadmapStanding standing)
    {
        ArgumentNullException.ThrowIfNull(standing);

        return standing.Next switch
        {
            NextKind.Item => string.Create(CultureInfo.InvariantCulture, $"Next: item {standing.NextNumber}."),
            NextKind.Complete => "Every item is done.",
            NextKind.Empty => "This roadmap has no items.",
            _ => "Nothing is ready to start: what is left is waiting on something or blocked.",
        };
    }
}
