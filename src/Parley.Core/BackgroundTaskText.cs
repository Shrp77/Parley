using System.Globalization;

namespace Parley.Core;

/// <summary>A task's words cut to their bound, and whether they were cut.</summary>
/// <param name="Text">The text.</param>
/// <param name="Cut">Whether anything was taken off the end.</param>
public readonly record struct CutText(string Text, bool Cut);

/// <summary>What the words of a background task are held to (SPEC.md §11.6).</summary>
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
    /// <summary>The most characters in a task's name (the person's bound).</summary>
    public const int LongestName = 64;

    /// <summary>The most characters in a task's description (the person's bound).</summary>
    public const int LongestDescription = 300;

    /// <summary>The most characters in a link.</summary>
    public const int LongestLink = 500;

    /// <summary>The most characters in a task's id.</summary>
    public const int LongestId = 64;

    /// <summary>The most tasks named for one agent, and the most an agent's own words are kept for; the count beside them
    /// still says how many are running.</summary>
    /// <remarks>Far past what a session runs at once. What it bounds is one line of a runner's output, or one agent
    /// describing ids without end, holding any amount: the node, the broker and the description store all stop here.</remarks>
    public const int MostTasks = 200;

    /// <summary>How many UTF-16 units a bounded text may spend, on average, on each character it is allowed.</summary>
    /// <remarks>
    /// <para>
    /// Most text spends one or two; a script whose characters are built of several marks (Devanagari, Thai) spends more,
    /// and is cut sooner than its count of characters alone would cut it. What it stops is one character built of thousands
    /// of marks, and words
    /// that, a few hundred tasks over, would make the registry a person reads too large to send: every agent's row is in
    /// one reply. A text past it is cut, with its ellipsis, where the units run out.
    /// </para>
    /// <para>
    /// A long run of joined emoji spends more and is cut sooner, which is a name drawn shorter, never a name lost.
    /// </para>
    /// </remarks>
    public const int UnitsPerCharacter = 2;

    /// <summary>Holds a task's name to its bound.</summary>
    /// <param name="text">The name as given, or null.</param>
    /// <returns>The name, neutralised, trimmed and cut; empty when nothing printable was given.</returns>
    public static CutText Name(string? text) => Bounded(text, LongestName);

    /// <summary>Holds a task's description to its bound.</summary>
    /// <param name="text">The description as given, or null.</param>
    /// <returns>The description, neutralised, trimmed and cut; empty when nothing printable was given.</returns>
    public static CutText Description(string? text) => Bounded(text, LongestDescription);

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

        // A named host with a dot in it: an address written as numbers, this machine, or a single word on the local network
        // is not where a ticket or document lives, and the console itself is one of them.
        if (link.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(link, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps
            || address.HostNameType != UriHostNameType.Dns
            || !address.IdnHost.Contains('.', StringComparison.Ordinal)
            || address.IsLoopback
            || LocalName(address.IdnHost)
            || address.UserInfo.Length > 0
            || !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Kept with its host in punycode, so a name in another script that looks like a familiar one (a Cyrillic а for a
        // Latin a) shows as what it is wherever the link is drawn. And kept as it will be followed, which escaping can make
        // longer than what was given, so it is held to the bound as kept, and reading a kept link again keeps it again.
        var kept = new UriBuilder(address) { Host = address.IdnHost }.Uri.AbsoluteUri;

        return kept.Length > LongestLink || !kept.All(char.IsAscii) || HistoryText.Unprintable(kept)
            ? null
            : new TaskLink(TaskLinkKind.Web, kept, address.IdnHost);
    }

    // Names that reach this machine whatever is written before them (RFC 6761): localhost and everything under it.
    private static bool LocalName(string host)
    {
        var name = host.TrimEnd('.');

        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase);
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

    private static CutText Bounded(string? text, int most)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new CutText(string.Empty, false);
        }

        // Cut by units first, so a megabyte is never neutralised to keep sixty-four characters of it.
        var units = most * UnitsPerCharacter;
        var cut = text.Length > units;
        var plain = HistoryText.Neutralised(cut ? HistoryText.Cut(text, units) : text).Trim();

        // Nothing printable is no text, however much of it there was.
        if (plain.Length == 0)
        {
            return new CutText(string.Empty, false);
        }

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
            plain = HistoryText.Cut(prefix, (most - 1) * UnitsPerCharacter).TrimEnd() + "…";
        }

        return new CutText(plain, cut);
    }
}
