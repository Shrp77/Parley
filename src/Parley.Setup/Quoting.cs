using System.Globalization;
using System.Text;

namespace Parley.Setup;

/// <summary>
/// Putting a value read out of a file into a line a person reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value this quotes came out of a file somebody edited</b> — a catalogue's role, a
/// manifest's path, an <c>owns</c> line recovered from a repository's history — and every line it
/// builds is read in a terminal. A value able to move the cursor or reverse the reading direction can
/// imitate the lines around it, which is worth more here than anywhere: the lines around it are
/// refusals, and a refusal is what somebody reads before deciding a file is safe to point a tool at.
/// </para>
/// <para>
/// <b>This is the third place in the repository to need it and the first to be shared.</b> The
/// terminal has <c>Safe</c> for what it draws, the console neutralises what it renders, and the
/// enrolment scan had a private copy of exactly this for the evidence it prints. The scan's copy is
/// now this one; the other two belong to surfaces that draw rather than print, and are left where
/// they are.
/// </para>
/// <para>
/// <b>Removed rather than replaced, and bounded.</b> A replacement character is still a character
/// somebody has to read past, and the values here are names and paths where a gap is more honest than
/// a symbol. The bound is because these end up in one line of a terminal: a path can be arbitrarily
/// long, and a refusal that scrolls its own reason off the screen has not refused anything.
/// </para>
/// </remarks>
public static class Quoting
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
                return kept.ToString(0, Longest - 1) + "…";
            }
        }

        return kept.ToString();
    }
}
