using System.Globalization;
using System.Text;

namespace Parley.Core;

/// <summary>
/// Text that renders as exactly what it says, on one line: the rule an ownership line is held to, and what an attach
/// reports about itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule in one place.</b> An ownership line is put into a roster every agent reads; the adapter, workspace and
/// resume handle an attach reports are put into the registry a person reads (SPEC.md §4.1.1). Anything able to break
/// the line, hide itself, or reorder what follows lets one value imitate the lines around it — and in a terminal an
/// escape sequence does more than imitate: it moves the cursor, erases what was printed, or writes to the clipboard.
/// </para>
/// <para>
/// <b>Refused by Unicode category, because a control-character test is not the rule.</b> <c>char.IsControl</c> covers
/// C0 and C1 and nothing else, so <c>U+2028 LINE SEPARATOR</c> and <c>U+2029 PARAGRAPH SEPARATOR</c> — line breaks
/// wherever this text is rendered — would go straight through it, as would <c>U+202E RIGHT-TO-LEFT OVERRIDE</c>, which
/// reverses the display of everything after it, and <c>U+200B</c> and <c>U+FEFF</c>, which are invisible. So: no
/// <see cref="UnicodeCategory.Control"/>, no <see cref="UnicodeCategory.Format"/>, and no line or paragraph separator.
/// Ordinary spaces stay, and so does every letter, accent, dash and emoji in every script.
/// </para>
/// <para>
/// Matched to the constraint on <c>agents.owns</c> in the schema, which refuses the same code points by range because
/// SQLite has no notion of a category. The two must agree: the schema is the enforcement, and a value this accepts and
/// the database rejects would produce a server that starts and then fails its own registration.
/// </para>
/// </remarks>
public static class PrintableLine
{
    /// <summary>Finds the first character that may not appear in one line of printable text.</summary>
    /// <param name="text">The candidate, or <see langword="null"/> for none.</param>
    /// <param name="found">The character, when there is one.</param>
    /// <param name="position">Where it is, counting from one in runes; zero when there is none.</param>
    /// <returns><see langword="true"/> if the text holds such a character.</returns>
    /// <remarks>
    /// <para>
    /// The position is in runes rather than UTF-16 units, so the number a person reads is the number of characters they
    /// would count.
    /// </para>
    /// <para>
    /// Applied to a rune rather than to a <see cref="char"/>, which is not a detail. Testing each UTF-16 unit puts both
    /// halves of a surrogate pair through the test, and both are category <c>Surrogate</c> — so a version that refused
    /// surrogates refused every emoji and every character outside the basic plane. A well-formed rune is never a
    /// surrogate, so the category is simply absent from the list.
    /// </para>
    /// </remarks>
    public static bool TryFindBreak(string? text, out Rune found, out int position)
    {
        found = default;
        position = 0;

        if (text is null)
        {
            return false;
        }

        var at = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            at++;

            if (Breaks(rune))
            {
                found = rune;
                position = at;

                return true;
            }
        }

        return false;
    }

    /// <summary>Whether one character may not appear in one line of printable text.</summary>
    /// <param name="rune">The character.</param>
    /// <returns><see langword="true"/> for a control, format, line-separator or paragraph-separator character.</returns>
    /// <remarks>
    /// The rule itself, typed once: what refuses a value here, and what the command line and setup neutralise before
    /// they print somebody else's text, each in its own way (one replaces, one removes), all ask this.
    /// </remarks>
    public static bool Breaks(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

    /// <summary>Whether a value an attach reports may be recorded, and why not when it may not.</summary>
    /// <param name="field">What the value is, as a person reads it: <c>workspace</c>, <c>adapter</c>.</param>
    /// <param name="value">What the attach carried, or <see langword="null"/> for nothing.</param>
    /// <param name="error">The refusal, naming the field, the character and where it is; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the value may be recorded.</returns>
    /// <remarks>
    /// Refused rather than stripped, as an ownership line is. An adapter reports these from its launch environment,
    /// which an agent can edit: silently removing an escape would admit the value and hide that somebody wrote one, and
    /// a workspace changed by a character is a different workspace to the comparison §4.1.1 makes.
    /// </remarks>
    public static bool IsReportable(string field, string? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (!TryFindBreak(value, out var found, out var position))
        {
            error = null;

            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"The {field} contains U+{found.Value:X4} at position {position}, which is not printable text, so this " +
            $"attach is refused. What an attach reports is shown to the person on one line, and anything that can " +
            $"break the line, hide itself, or reorder what follows can imitate or rewrite the lines around it.");

        return false;
    }
}
