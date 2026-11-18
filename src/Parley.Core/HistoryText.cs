using System.Text;

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
public static class HistoryText
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

        return text.EnumerateRunes().Any(r => r == Rune.ReplacementChar || PrintableLine.Breaks(r));
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
            if (rune == Rune.ReplacementChar || PrintableLine.Breaks(rune))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString().Replace("[parley:", "[parley :", StringComparison.Ordinal);
    }
}
