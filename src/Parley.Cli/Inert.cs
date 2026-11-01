using System.Globalization;
using System.Text;

namespace Parley.Cli;

/// <summary>
/// Somebody else's text, made safe to print as part of one line.
/// </summary>
/// <remarks>
/// <para>
/// <b>The command line's one neutraliser.</b> Everything this tool prints that it did not compose goes through
/// <see cref="Line"/>: a workspace, adapter or resume handle an agent reported, a file name somebody placed, a broker's
/// detail, an exception's message, a reason a broker stored. An agent can edit its own launch configuration, so any of
/// those can hold an escape sequence, and a terminal obeys one: it moves the cursor up and erases the lines a person
/// was reading, or writes to their clipboard (lens 2 run 135).
/// </para>
/// <para>
/// <b>One for one, with a visible stand-in.</b> A line that held a control character reads as one that held one, and
/// the columns around it stay where they were. The terminal's <c>Safe</c> does the same for what it draws; this is the
/// copy for what is printed. Setup's <c>Quoting</c> removes instead, because it quotes names and paths in refusals,
/// where a gap reads more honestly than a symbol.
/// </para>
/// </remarks>
internal static class Inert
{
    /// <summary>Makes somebody else's text safe to print as part of one line.</summary>
    /// <param name="text">The text, or <see langword="null"/> for none.</param>
    /// <returns>The text with every control, format, line-separator and paragraph-separator character replaced.</returns>
    internal static string Line(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flat = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            flat.Append(category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? "?"
                : rune.ToString());
        }

        return flat.ToString();
    }
}
