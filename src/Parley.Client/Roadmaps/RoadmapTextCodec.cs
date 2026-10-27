using System.Globalization;
using System.Text.RegularExpressions;
using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// A roadmap as plain text a person can edit in any text field, and back (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>One format for every surface that edits a list.</b> The terminal's edit field and the web console's text area both
/// hold a roadmap as these lines, so a list copied from one is accepted by the other and
/// there is one place that says what a line means. It is deliberately the same shape an agent proposes in: a title, then
/// one line per item with <c>for:</c> and <c>needs:</c> only where they are known.
/// </para>
/// <para>
/// <b>Labels, not positions, name an item while it is being edited.</b> A line begins with a number and a dot, and
/// <c>needs:</c> names the numbers of other lines. The order of the lines is the order of the roadmap, whatever the
/// numbers say, so moving a line is moving a line and nothing has to be renumbered for the list to mean what it did.
/// What a move can break is an item that needs one now below it, which <see cref="RoadmapRules"/> reports.
/// </para>
/// <para>
/// <b><c>item:</c> is an item's identity</b>, present when an existing roadmap's items are written out, so that an edit
/// that rewords or reorders one still means the same item and keeps its conversation. A line with none is a new item.
/// </para>
/// </remarks>
public static partial class RoadmapTextCodec
{
    private const string Separator = " | ";

    /// <summary>A bar that is part of an item's words, as written out: never the start of a field.</summary>
    private const string EscapedBar = @"\|";

    [GeneratedRegex(@"^\s*(\d+)\s*[.)]\s*(.*)$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex ItemLine();

    /// <summary>Writes content as text.</summary>
    /// <param name="content">The title and items.</param>
    /// <returns>The title on its own line, then one line per item.</returns>
    public static string Write(RoadmapContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = new List<string> { "title: " + content.Title };

        for (var at = 0; at < content.Items.Count; at++)
        {
            var item = content.Items[at];
            // **The item's own words can never be read back as a field.** They may be an agent's, and the broker refuses
            // a proposal that claims item numbers: `| item: 1` in its words, written out bare, would claim item 1's
            // conversation and its open work when the person confirms the text. Every bar in the words is escaped.
            var line = string.Create(CultureInfo.InvariantCulture, $"{at + 1}. {item.Text.Replace("|", EscapedBar, StringComparison.Ordinal)}");

            if (item.IntendedAgent is { } agent)
            {
                line += Separator + "for: " + agent.Value;
            }

            if (item.Needs.Count > 0)
            {
                line += Separator + "needs: " + string.Join(", ", item.Needs.Order().Select(
                    n => n.ToString(CultureInfo.InvariantCulture)));
            }

            if (item.Number is { } number)
            {
                line += Separator + string.Create(CultureInfo.InvariantCulture, $"item: {number}");
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    /// <summary>Reads text back into content.</summary>
    /// <param name="text">What a person wrote.</param>
    /// <returns>
    /// The content and no problem when it is whole; a sentence naming the first thing wrong, and content where it could
    /// still be read, when it is not.
    /// </returns>
    /// <remarks>
    /// The sentence is a line's own: a person editing twenty items is told which one, not that "the list" is wrong.
    /// What <see cref="RoadmapRules"/> says about the content is returned the same way, so a surface needs one place to
    /// show a problem and the broker's refusal is not the first time somebody learns of it.
    /// </remarks>
    public static (RoadmapContent? Content, string? Problem) Parse(string? text)
    {
        var lines = (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n');

        string? title = null;
        var drafts = new List<(int Label, string Text, AgentName? Agent, List<int> Needs, int? Number)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var shown = index + 1;

            if (title is null)
            {
                if (!line.TrimStart().StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, $"Line {shown}: begin with the title, as 'title: a few words'.");
                }

                title = line.TrimStart()["title:".Length..].Trim();

                continue;
            }

            var match = ItemLine().Match(line);

            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var label))
            {
                return (null, $"Line {shown}: start each item with a number and a dot, like '1. Add the export'.");
            }

            var parts = match.Groups[2].Value.Split(Separator);
            var itemText = parts[0];
            AgentName? agent = null;
            var needs = new List<int>();
            int? number = null;

            foreach (var part in parts.Skip(1))
            {
                var trimmed = part.Trim();

                if (Field(trimmed, "for") is { } who)
                {
                    if (who.Length == 0)
                    {
                        agent = null;
                    }
                    else if (AgentName.TryCreate(who, out var named))
                    {
                        agent = named;
                    }
                    else
                    {
                        return (null, $"Line {shown}: '{who}' is not an agent name. Names are role@machine.");
                    }
                }
                else if (Field(trimmed, "needs") is { } wanted)
                {
                    foreach (var token in wanted.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var needed))
                        {
                            return (null, $"Line {shown}: 'needs' lists item numbers, like 'needs: 1, 2'.");
                        }

                        needs.Add(needed);
                    }
                }
                else if (Field(trimmed, "item") is { } existing)
                {
                    if (!int.TryParse(existing, NumberStyles.None, CultureInfo.InvariantCulture, out var kept))
                    {
                        return (null, $"Line {shown}: 'item' is the number of an item the roadmap already has.");
                    }

                    number = kept;
                }
                else
                {
                    // Not a field: a separator that was part of the item's own words. Put back as written.
                    itemText += Separator + part;
                }
            }

            if (drafts.Any(d => d.Label == label))
            {
                return (null, $"Line {shown}: number {label} is used twice. Each item has a number of its own.");
            }

            drafts.Add((label, itemText.Replace(EscapedBar, "|", StringComparison.Ordinal).Trim(), agent, needs, number));
        }

        if (title is null)
        {
            return (null, "Begin with the title, as 'title: a few words'.");
        }

        var positions = drafts.Select((d, at) => (d.Label, Position: at + 1)).ToDictionary(p => p.Label, p => p.Position);
        var items = new List<RoadmapItemDraft>();

        foreach (var draft in drafts)
        {
            var resolved = new List<int>();

            foreach (var needed in draft.Needs)
            {
                if (!positions.TryGetValue(needed, out var position))
                {
                    return (null, $"Item {draft.Label} needs {needed}, and no line has that number.");
                }

                resolved.Add(position);
            }

            items.Add(new RoadmapItemDraft(draft.Number, draft.Text, draft.Agent, [.. resolved.Distinct().Order()]));
        }

        var content = new RoadmapContent(title, items);

        return (content, RoadmapRules.Problem(content));
    }

    /// <summary>Reads <c>key: value</c> where the key is the one asked for.</summary>
    private static string? Field(string part, string key) =>
        part.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase) ? part[(key.Length + 1)..].Trim() : null;
}
