namespace Parley.Core;

/// <summary>
/// Whether a roadmap's content is whole, applied to the person's final words wherever they are confirmed or
/// edited (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>It judges content, not a change.</b> A trigger in storage sees one dependency being inserted and cannot see a
/// reorder, so a person moving item 4 above item 2 would leave item 3 needing something later than itself without
/// any single write being wrong. The whole list is checked instead, which is what makes a removed item never
/// the dependency of a live one: the person's final content cannot name one.
/// </para>
/// <para>
/// What it cannot check is that an intended agent is in the project, because Core knows no project. The broker
/// asks the registry; this is the half that needs no I/O, shared by the broker and by a surface that would rather
/// tell a person before sending.
/// </para>
/// </remarks>
public static class RoadmapRules
{
    /// <summary>Says what is wrong with a roadmap's content, or nothing.</summary>
    /// <param name="content">The title and items.</param>
    /// <returns>A sentence naming the first problem, or <see langword="null"/> if the content is whole.</returns>
    public static string? Problem(RoadmapContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!RoadmapText.IsValid(content.Title, "The title", out var titleError))
        {
            return titleError;
        }

        if (content.Items.Count == 0)
        {
            return "A roadmap has at least one item.";
        }

        for (var at = 0; at < content.Items.Count; at++)
        {
            var position = at + 1;
            var item = content.Items[at];

            if (!RoadmapText.IsValid(item.Text, $"Item {position}", out var textError))
            {
                return textError;
            }

            var seen = new HashSet<int>();

            foreach (var needed in item.Needs)
            {
                if (needed < 1 || needed >= position)
                {
                    return $"Item {position} needs item {needed}, which is not an earlier item. An item " +
                        "can need only items that come before it, so order the list first.";
                }

                if (!seen.Add(needed))
                {
                    return $"Item {position} names item {needed} twice.";
                }
            }

            if (item.Number is { } number && content.Items.Count(i => i.Number == number) > 1)
            {
                return $"Item number {number} appears more than once.";
            }
        }

        return null;
    }
}
