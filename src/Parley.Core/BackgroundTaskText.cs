using System.Globalization;

namespace Parley.Core;

/// <summary>Text cut to a bound, and whether it was cut.</summary>
/// <param name="Text">The text.</param>
/// <param name="Cut">Whether anything was taken off the end.</param>
public readonly record struct BoundedText(string Text, bool Cut);

/// <summary>What the words of a background task are held to (feature 043, SPEC.md §11.6).</summary>
/// <remarks>
/// <para>
/// <b>One place.</b> A task's name and description come from the runner's output and from the agent, and are drawn in the
/// person's own view on every surface. The broker puts every one through here before it holds it, so no surface has to
/// trust the node or the agent to have done it. A character that could break, hide or reorder a line becomes a space, by
/// <see cref="HistoryText.Neutralised"/>'s rule and no second list.
/// </para>
/// <para>
/// <b>Bounds are in characters a person counts</b> (text elements), so an emoji is one. A single text element can be any
/// number of units long (a letter under five thousand accents is one), so the units are bounded too.
/// </para>
/// <para>
/// <b>A link is the one thing a person may follow</b>, so it is matched against four fixed shapes and anything else is
/// dropped. Nothing in a task is parsed for meaning; a link is only recognised or not.
/// </para>
/// </remarks>
public static class BackgroundTaskText
{
    /// <summary>The most characters in a task's name (the person's bound, exchange #58).</summary>
    public const int LongestName = 64;

    /// <summary>The most characters in a task's description (the person's bound, exchange #58).</summary>
    public const int LongestDescription = 300;

    /// <summary>The most characters in a link.</summary>
    public const int LongestLink = 500;

    /// <summary>The most characters in a task's id.</summary>
    public const int LongestId = 64;

    /// <summary>How many UTF-16 units a bounded text may spend on each character it is allowed.</summary>
    /// <remarks>Past any real character (an emoji with a skin tone and a joiner is under twelve); what it stops is one
    /// character built of thousands of marks.</remarks>
    public const int UnitsPerCharacter = 16;

    /// <summary>Holds a task's name to its bound.</summary>
    /// <param name="text">The name as given, or null.</param>
    /// <returns>The name, neutralised, trimmed and cut; empty when nothing printable was given.</returns>
    public static BoundedText Name(string? text) => Bounded(text, LongestName);

    /// <summary>Holds a task's description to its bound.</summary>
    /// <param name="text">The description as given, or null.</param>
    /// <returns>The description, neutralised, trimmed and cut; empty when nothing printable was given.</returns>
    public static BoundedText Description(string? text) => Bounded(text, LongestDescription);

    /// <summary>Whether text is a task id the runner could have given: 1 to 64 letters, digits, hyphens and underscores.</summary>
    /// <param name="id">The candidate.</param>
    /// <returns>True when it is.</returns>
    public static bool IsTaskId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > LongestId)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a link in one of the shapes Parley shows.</summary>
    /// <param name="text">The link as given, or null.</param>
    /// <returns>The link, or null when it is none of them.</returns>
    /// <remarks>
    /// <c>exchange #N</c> or <c>#N</c>; <c>conversation:&lt;id&gt;</c>; <c>file:&lt;name&gt;</c> for a file placed in
    /// Parley; or an absolute <c>https</c> address with a host and no user name or password in it. An address that hid
    /// where it went, or a scheme that runs something (<c>javascript:</c>, <c>data:</c>), would be a door into the person's
    /// own view, so only these are kept.
    /// </remarks>
    public static TaskLink? Link(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > LongestLink || HistoryText.Unprintable(text))
        {
            return null;
        }

        var link = text.Trim();

        if (Exchange(link) is { } number)
        {
            return new TaskLink(TaskLinkKind.Exchange, number.ToString(CultureInfo.InvariantCulture));
        }

        if (link.StartsWith("conversation:", StringComparison.Ordinal))
        {
            var id = link["conversation:".Length..];

            return ConversationId.IsValid(id) ? new TaskLink(TaskLinkKind.Conversation, id) : null;
        }

        if (link.StartsWith("file:", StringComparison.Ordinal))
        {
            var name = link["file:".Length..];

            return SharedFileName.TryCreate(name, out var placed) ? new TaskLink(TaskLinkKind.File, placed.Value) : null;
        }

        if (link.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(link, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps
            || address.HostNameType is UriHostNameType.Unknown
            || string.IsNullOrEmpty(address.Host)
            || address.UserInfo.Length > 0
            || !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new TaskLink(TaskLinkKind.Web, address.AbsoluteUri, address.IdnHost);
    }

    private static long? Exchange(string link)
    {
        var digits = link.StartsWith('#') ? link[1..]
            : link.StartsWith("exchange #", StringComparison.OrdinalIgnoreCase) ? link["exchange #".Length..]
            : null;

        if (digits is not { Length: > 0 and <= 18 } || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var number = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

        return number > 0 ? number : null;
    }

    private static BoundedText Bounded(string? text, int most)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new BoundedText(string.Empty, false);
        }

        // Cut by units first, so a megabyte is never neutralised to keep sixty-four characters of it.
        var units = most * UnitsPerCharacter;
        var cut = text.Length > units;
        var plain = HistoryText.Neutralised(cut ? HistoryText.Cut(text, units) : text).Trim();
        var elements = StringInfo.GetTextElementEnumerator(plain);
        var kept = 0;
        var end = 0;

        while (elements.MoveNext())
        {
            if (kept == most)
            {
                cut = true;
                break;
            }

            kept++;
            end = elements.ElementIndex + elements.GetTextElement().Length;
        }

        if (cut)
        {
            // The ellipsis takes the last place.
            var keep = new StringInfo(plain[..end]);
            var prefix = keep.LengthInTextElements >= most ? keep.SubstringByTextElements(0, most - 1) : keep.String;
            plain = HistoryText.Cut(prefix.TrimEnd(), (most - 1) * UnitsPerCharacter) + "…";
        }

        return new BoundedText(plain, cut);
    }
}
