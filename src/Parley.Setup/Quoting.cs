using System.Globalization;
using System.Text;

namespace Parley.Setup;

/// <summary>
/// Putting a value read out of a file into a line a person reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value this quotes came out of a file somebody edited</b> — a catalogue's role, a
/// manifest's path, a line of a repository's README — and every line it
/// builds is read in a terminal. A value able to move the cursor or reverse the reading direction can
/// imitate the lines around it, which is worth more here than anywhere: the lines around it are
/// refusals, and a refusal is what somebody reads before deciding a file is safe to point a tool at.
/// </para>
/// <para>
/// <b>The terminal has <c>Safe</c> for what it draws and the console neutralises what it
/// renders;</b> those belong to surfaces that draw rather than print, and this is the shared copy
/// for what is printed.
/// </para>
/// <para>
/// <b>Removed rather than replaced, and bounded.</b> A replacement character is still a character
/// somebody has to read past, and the values here are names and paths where a gap is more honest than
/// a symbol. The bound is because these end up in one line of a terminal: a path can be arbitrarily
/// long, and a refusal that scrolls its own reason off the screen has not refused anything.
/// </para>
/// </remarks>
internal static class Quoting
{
    /// <summary>How much of one value a line will carry.</summary>
    public const int Longest = 160;

    /// <summary>One value, safe to put in a line somebody reads.</summary>
    /// <param name="value">Whatever was in the file.</param>
    /// <returns>The same value without anything that could rearrange the line.</returns>
    /// <remarks>
    /// Control, format, line-separator and paragraph-separator categories all go. Format is the one
    /// worth naming: it carries the bidirectional overrides, which are invisible and reverse the
    /// order of what follows — a role called <c>orders</c> can be made to read as something else
    /// entirely without a single visible character being wrong.
    /// </remarks>
    public static string Flat(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var kept = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(rune.Value) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            kept.Append(rune);

            if (kept.Length > Longest)
            {
                // The bound is counted in runes and applied in chars, and those are not the same unit. A
                // rune outside the basic plane is two chars, so an index taken at the bound can land
                // between the halves of one, and half a surrogate pair renders as the replacement glyph
                // exactly when a value is being quoted in a refusal.
                var cut = Longest - 1;

                if (char.IsLowSurrogate(kept[cut]))
                {
                    cut--;
                }

                return kept.ToString(0, cut) + "…";
            }
        }

        return kept.ToString();
    }

    /// <summary>One value, delimited, and unable to add delimiters of its own.</summary>
    /// <param name="value">Whatever was in the file.</param>
    /// <returns>The value between quotes, carrying none.</returns>
    /// <remarks>
    /// <para>
    /// <b>The quotes are part of the neutralisation, which is why they are here and not at each
    /// call site.</b> A value carrying the delimiter reads as several quoted values where there is one: a role of
    /// <c>orders' in /srv/other.json. Project 'beta</c> turns one refusal into what looks like a
    /// complete and different one, naming a file the value's author chose — and a refusal is what
    /// somebody reads before deciding which file is safe to point a tool at. The same shape as a forged
    /// transcript header, one layer up.
    /// </para>
    /// <para>
    /// <b>Removed here rather than in <see cref="Flat"/>, because an apostrophe is legitimate in the
    /// values <see cref="Flat"/> is used for undelimited</b> — a path may contain one and a message
    /// naming a file has to name the real one. Nothing that is quoted can legitimately carry it:
    /// project names and keys are checked against a grammar that excludes it, and roles, agent names
    /// and broker addresses have none either.
    /// </para>
    /// </remarks>
    public static string Quoted(string? value) =>
        Delimiter + Flat(value).Replace(Delimiter, string.Empty, StringComparison.Ordinal) + Delimiter;

    /// <summary>What a quoted value sits between.</summary>
    private const string Delimiter = "'";
}
