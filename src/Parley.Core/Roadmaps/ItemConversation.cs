using System.Globalization;

namespace Parley.Core;

/// <summary>
/// The name derived for the conversation that belongs to one roadmap item (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// Derived from two small numbers, <c>rm&lt;roadmap&gt;-&lt;item&gt;</c>, so the name is a legal conversation
/// identifier by construction — letters, digits and a hyphen are inside every rule an identifier has — and unique
/// among derived names. It is predictable, so an agent can open a conversation under it before the item exists; the
/// store then reserves the first free name after it instead. The reservation is made when the item is confirmed, and
/// the conversation comes to exist when work on the item starts.
/// </para>
/// <para>
/// <b>A name is never what recognises an item's conversation.</b> That is the reservation in storage. A prefix
/// would be a way for an agent to make an ordinary conversation <em>look</em> like an item's, and §13 says a
/// payload is inert data. Nothing parses a name back into a claim about a roadmap.
/// </para>
/// </remarks>
public static class ItemConversation
{
    private const string Prefix = "rm";

    /// <summary>The conversation name derived for one item.</summary>
    /// <param name="roadmapNumber">The roadmap's number, one or more.</param>
    /// <param name="itemNumber">The item's number within it, one or more.</param>
    /// <returns>The name, for example <c>rm3-2</c>.</returns>
    public static string Name(int roadmapNumber, int itemNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roadmapNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemNumber, 1);

        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{roadmapNumber}-{itemNumber}");
    }
}
