using System.Globalization;

namespace Parley.Core;

/// <summary>
/// The name of the conversation that belongs to one roadmap item (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// Derived from two small numbers, <c>rm&lt;roadmap&gt;-&lt;item&gt;</c>, so the name is a legal conversation
/// identifier by construction — letters, digits and a hyphen are inside every rule an identifier has — and unique
/// without being looked up. It is reserved when a roadmap is confirmed, and the conversation comes to exist when
/// work on the item starts.
/// </para>
/// <para>
/// <b>A name is never what recognises an item's conversation.</b> That is the reservation in storage. A prefix
/// would be a way for an agent to make an ordinary conversation <em>look</em> like an item's, and §13 says a
/// payload is inert data. Nothing here parses a name back into a claim about a roadmap; <see cref="TryParse"/>
/// exists for a surface that has only an identifier in its hand and means to ask the broker whether it is one.
/// </para>
/// </remarks>
public static class ItemConversation
{
    private const string Prefix = "rm";

    /// <summary>The conversation name for one item.</summary>
    /// <param name="roadmapNumber">The roadmap's number, one or more.</param>
    /// <param name="itemNumber">The item's number within it, one or more.</param>
    /// <returns>The name, for example <c>rm3-2</c>.</returns>
    public static string Name(int roadmapNumber, int itemNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roadmapNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemNumber, 1);

        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{roadmapNumber}-{itemNumber}");
    }

    /// <summary>Reads the two numbers back out of a name this class made.</summary>
    /// <param name="name">A conversation identifier.</param>
    /// <param name="roadmapNumber">The roadmap's number, when it parses.</param>
    /// <param name="itemNumber">The item's number, when it parses.</param>
    /// <returns>Whether the identifier has the shape of one this class makes.</returns>
    /// <remarks>
    /// A shape and nothing more. An identifier with this shape is not thereby an item's conversation; only the
    /// broker's reservation says so.
    /// </remarks>
    public static bool TryParse(string? name, out int roadmapNumber, out int itemNumber)
    {
        roadmapNumber = 0;
        itemNumber = 0;

        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var dash = name.IndexOf('-', StringComparison.Ordinal);

        if (dash > Prefix.Length
            && int.TryParse(name.AsSpan(Prefix.Length, dash - Prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var roadmap)
            && int.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var item)
            && roadmap >= 1
            && item >= 1
            && string.Equals(name, Name(roadmap, item), StringComparison.Ordinal))
        {
            roadmapNumber = roadmap;
            itemNumber = item;

            return true;
        }

        return false;
    }
}
