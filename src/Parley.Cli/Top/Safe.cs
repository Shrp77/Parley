using System.Text;

namespace Parley.Cli.Top;

/// <summary>
/// Turning text this program did not write into text a terminal will not act on.
/// </summary>
/// <remarks>
/// <para>
/// Almost everything this surface draws was written by somebody else: agent names, conversation
/// identifiers, message payloads, project names from a manifest, refusals in a broker's own words,
/// and lines out of a log. Any of those reaching the screen unexamined lets its author choose how
/// the operator's screen is drawn.
/// </para>
/// <para>
/// <b>What it neutralises changed with the toolkit, and the hazard did not.</b> The Spectre
/// version escaped square brackets, because that library reads them as style tags. This one has no
/// markup to escape — the toolkit draws strings as characters — so what is left is the older and
/// more serious version of the same thing: control characters. An escape sequence in a log line
/// moves the cursor, repaints rows the surface believes it owns, and can put text on screen that
/// appears to have come from the surface itself. A carriage return alone is enough to overwrite a
/// line already drawn.
/// </para>
/// <para>
/// So every character below the printable range is replaced rather than filtered out. Removing
/// them would silently change the length of somebody's identifier, which matters here: an agent's
/// name typed back is what stands between them and being purged, and two names that differ only by
/// a character this dropped would compare equal on screen and unequal in the check.
/// </para>
/// <para>
/// A type of its own rather than a helper on whichever view needed it first. It was
/// <c>ProjectList.Escape</c>, called from eleven files that have nothing to do with listing
/// projects, which is how a security property comes to look like a formatting convenience.
/// </para>
/// </remarks>
internal static class Safe
{
    /// <summary>What a control character is drawn as.</summary>
    /// <remarks>
    /// A visible stand-in rather than nothing, so a line that contained one reads as a line that
    /// contained one. U+FFFD is what a terminal font will have.
    /// </remarks>
    internal const char Replacement = '�';

    /// <summary>Makes text inert, so a terminal shows it rather than obeying it.</summary>
    /// <param name="text">Whatever came from a manifest, a broker, an agent or a log.</param>
    /// <returns>The same text, one character for one, with nothing in it a terminal will act on.</returns>
    internal static string Text(string? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var made = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            // Tab is kept: it is laid out rather than obeyed, and log lines are full of them.
            // A newline is not, because a single value drawn on one row must not become two.
            made.Append(character == '\t' || !char.IsControl(character) ? character : Replacement);
        }

        return made.ToString();
    }

    /// <summary>
    /// Makes text inert while keeping the lines this program laid out.
    /// </summary>
    /// <param name="text">Whatever came from a manifest, a broker, an agent or a log.</param>
    /// <returns>The same text, line by line, with nothing in it a terminal will act on.</returns>
    /// <remarks>
    /// <para>
    /// For the places where the structure is ours and the content is somebody else's: a transcript
    /// laid out by <c>Transcript.Write</c>, or a description this surface composed around values it
    /// read. <see cref="Text"/> would replace the line breaks holding those together and produce
    /// one unreadable row.
    /// </para>
    /// <para>
    /// A line break inside a value survives as a line break, and that is the deliberate limit of
    /// this: a message can put its words on two rows. What it cannot do is move the cursor, which
    /// is the difference between untidy and dishonest.
    /// </para>
    /// </remarks>
    internal static string Lines(string? text) =>
        text is null
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                text.ReplaceLineEndings("\n").Split('\n').Select(Text));
}
