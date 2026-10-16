using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>
/// A stable colour and a short badge for each agent in a transcript.
/// </summary>
/// <remarks>
/// <para>
/// Decoration, and only decoration. The colour helps an eye follow one agent down a long
/// conversation; it never carries a fact. Every message names its sender and its recipient in
/// words, and the visibility annotation names the agents who were not party to it — so a reader
/// who sees no colour at all loses nothing but convenience. That is the rule the whole view is
/// built to keep, and a palette is exactly where it would be easiest to break.
/// </para>
/// <para>
/// Derived from the name rather than from position in a list, so an agent is the same colour on
/// every page and in every conversation. Position would repaint the transcript whenever somebody
/// new joined, which is the one thing that would make the colour actively misleading.
/// </para>
/// </remarks>
public static class Palette
{
    /// <summary>How many distinct slots the stylesheet defines.</summary>
    /// <remarks>
    /// Eight. More would make neighbouring hues too close to tell apart, which is worse than two
    /// agents sharing one — a collision is survivable because the name is always written, and an
    /// indistinguishable pair of colours quietly invites the reader to trust the wrong one.
    /// </remarks>
    public const int Slots = 8;

    /// <summary>Which colour slot an agent occupies.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>A number from zero to <see cref="Slots"/> minus one.</returns>
    /// <remarks>
    /// An ordinal hash of the whole name, folded into the slot count. Deliberately not
    /// <see cref="string.GetHashCode()"/>: that is randomised per process in .NET, so an agent
    /// would change colour every time the console restarted.
    /// </remarks>
    public static int SlotFor(AgentName agent)
    {
        var hash = 2166136261;

        foreach (var character in agent.Value)
        {
            hash = (hash ^ character) * 16777619;
        }

        return (int)(hash % Slots);
    }

    /// <summary>The short badge shown beside a message, in place of a picture.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>One or two characters.</returns>
    /// <remarks>
    /// Taken from the role rather than the whole name, because every agent on one machine shares
    /// a machine suffix and a badge reading the same two letters for all of them would be worse
    /// than none. A hyphenated role gives one letter from each half, so <c>sync-data</c> reads
    /// <c>sd</c> and <c>database</c> reads <c>da</c>.
    /// </remarks>
    public static string BadgeFor(AgentName agent)
    {
        var role = agent.Role;

        if (role.Length == 0)
        {
            return "?";
        }

        var parts = role.Split(['-', '.', '_'], StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 1
            ? string.Concat(parts[0][..1], parts[1][..1]).ToUpperInvariant()
            : role[..Math.Min(2, role.Length)].ToUpperInvariant();
    }
}
