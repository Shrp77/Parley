using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Parley.Core;

/// <summary>How words and snippets of the past are folded and made safe to show (SPEC.md §10.1, §13).</summary>
/// <remarks>
/// <para>
/// One place, so a search, a snippet and a person's view of what was searched for agree. What counts as not printable is
/// <see cref="PrintableLine"/>'s rule and no second list: control, format (which holds the tag characters used to hide
/// text) and line- and paragraph-separating characters.
/// </para>
/// <para>
/// A snippet is the one piece of another participant's text that reaches a model with no envelope, so it is made safe here
/// before it leaves: every character that could break, hide or reorder a line becomes a space, one for one, and the text that
/// opens an envelope is spelled so it cannot be mistaken for one.
/// </para>
/// </remarks>
public static partial class HistoryText
{
    /// <summary>Lower-cases the letters A to Z and nothing else, as the store's own <c>lower</c> does.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The same text, the same length.</returns>
    public static string AsciiLower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
            }
        });
    }

    /// <summary>Whether text holds a character that could break, hide or reorder a line, or is not well-formed.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when it should be refused.</returns>
    public static bool Unprintable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.EnumerateRunes().Any(NotPlain);
    }

    /// <summary>Makes text another participant wrote safe to show beside a line of one's own.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with each unprintable character a space, and an envelope's opening made plain.</returns>
    public static string Neutralised(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            if (NotPlain(rune))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return EnvelopeOpening().Replace(builder.ToString(), "[parley :");
    }

    /// <summary>Cuts text to a number of UTF-16 units without splitting a surrogate pair.</summary>
    /// <param name="text">The text.</param>
    /// <param name="units">The most units to keep.</param>
    /// <returns>The text, or its start.</returns>
    public static string Cut(string text, int units)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= units)
        {
            return text;
        }

        return char.IsHighSurrogate(text[units - 1]) ? text[..(units - 1)] : text[..units];
    }

    /// <summary>Whether a character is anything but plain visible text or an ordinary space.</summary>
    /// <remarks>
    /// <see cref="PrintableLine"/>'s rule (control, format, line and paragraph separators) and what it leaves: the characters
    /// Unicode marks default-ignorable, which draw nothing and are used to carry hidden text (the variation selectors, the
    /// Hangul fillers, the Braille blank and a few marks), other spaces than the ordinary one, private-use characters and
    /// unassigned ones.
    /// </remarks>
    private static bool NotPlain(Rune rune)
    {
        if (rune == Rune.ReplacementChar || PrintableLine.Breaks(rune))
        {
            return true;
        }

        var v = rune.Value;

        if (v is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2800 or 0x3164 or 0xFFA0 or 0xFFFC
            || v is >= 0x180B and <= 0x180F
            || v is >= 0xFE00 and <= 0xFE0F
            || v is >= 0xE0100 and <= 0xE01EF)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse => true,
            UnicodeCategory.SpaceSeparator => v != ' ',
            _ => false,
        };
    }

    [GeneratedRegex(@"\[parley:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnvelopeOpening();
}
