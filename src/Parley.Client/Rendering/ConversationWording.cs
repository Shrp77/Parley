using System.Globalization;
using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>What every surface says about a conversation's parent and what moved (SPEC.md §5.0).</summary>
/// <remarks>One place, so the terminal, the web console and an export cannot word a move differently.</remarks>
public static class ConversationWording
{
    /// <summary>What a list adds after a conversation's name when it has a parent.</summary>
    /// <param name="parent">The parent.</param>
    /// <returns>The words, beginning with the separator. A conversation's name cannot hold a space, so no name can imitate them.</returns>
    public static string SpinoffSuffix(string parent) => $" - spinoff from {parent}";

    /// <summary>A conversation's name as a list shows it.</summary>
    /// <param name="id">Its name.</param>
    /// <param name="parent">Its parent, or <see langword="null"/>.</param>
    /// <returns>The name alone, or the name and its parent. Never used as the name itself: agents, links and exports keep that.</returns>
    public static string Listed(string id, string? parent) =>
        parent is null ? id : id + SpinoffSuffix(parent);

    /// <summary>A line about one move, as read from one of its two conversations.</summary>
    /// <param name="move">The move.</param>
    /// <param name="conversation">The conversation being read.</param>
    /// <param name="zone">The zone to say the time in, or null for this machine's.</param>
    /// <returns>A sentence naming the other one.</returns>
    /// <remarks>With its date, because it sits among messages in the broker's order and may be days younger than they are.</remarks>
    public static string MoveLine(MoveRecord move, string conversation, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(move);

        var what = Amount(move);
        var when = TimeZoneInfo.ConvertTime(move.MovedUtc, zone ?? TimeZoneInfo.Local).ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

        return string.Equals(move.Source, conversation, StringComparison.Ordinal)
            ? $"{what} moved from here to {move.Destination} on {when}."
            : $"{what} moved here from {move.Source} on {when}.";
    }

    /// <summary>The other conversation a move names, as read from one of its two.</summary>
    /// <param name="move">The move.</param>
    /// <param name="conversation">The conversation being read.</param>
    /// <returns>The other's name.</returns>
    public static string OtherSide(MoveRecord move, string conversation)
    {
        ArgumentNullException.ThrowIfNull(move);

        return string.Equals(move.Source, conversation, StringComparison.Ordinal) ? move.Destination : move.Source;
    }

    /// <summary>A line about one move for a file somebody takes away, in UTC and naming everything.</summary>
    /// <param name="move">The move.</param>
    /// <returns>The sentence.</returns>
    public static string MoveRecordLine(MoveRecord move)
    {
        ArgumentNullException.ThrowIfNull(move);

        var exchanges = string.Join(", ", move.Exchanges.Select(id => id.ToString(CultureInfo.InvariantCulture)));

        return $"{Amount(move)} (exchange {exchanges}) moved from {move.Source} to {move.Destination} "
            + $"by {move.MovedBy} on {move.MovedUtc.ToString("u", CultureInfo.InvariantCulture)}.";
    }

    /// <summary>A line about a settled pair split across two conversations (SPEC.md §5.6).</summary>
    /// <param name="pair">The pair.</param>
    /// <returns>The sentence.</returns>
    public static string SettledLine(SettledElsewhere pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Exchange {pair.Exchange} was settled by exchange {pair.SettledBy}. One of them is now in {pair.Conversation}.");
    }

    private static string Amount(MoveRecord move)
    {
        var exchanges = move.Exchanges.Count;
        var messages = move.Messages;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{exchanges} exchange{(exchanges == 1 ? string.Empty : "s")} ({messages} message{(messages == 1 ? string.Empty : "s")})");
    }
}
