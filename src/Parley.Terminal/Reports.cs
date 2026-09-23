namespace Parley.Terminal;

/// <summary>
/// What a screen that did several things says about them on the message line.
/// </summary>
/// <remarks>
/// <para>
/// <b>One paragraph of sentences, never lines.</b> Every report here was joined with line breaks, and the message line
/// draws every control character as <c>�</c> on purpose — a line break in a broker's text is how 0.25.1's forged second
/// line was made, so <see cref="Shell.Said"/> makes one visible rather than obeying it. On Windows each break reached the
/// owner's screen as <c>��</c> between two halves of a sentence. The line wraps on its own; what separates the parts is
/// punctuation, which nothing can mistake for structure.
/// </para>
/// <para>
/// A type of its own because five screens built the same report the same wrong way, and the owner found it on one. A
/// helper on that one would have left four.
/// </para>
/// </remarks>
internal static class Reports
{
    /// <summary>The parts of a report as one paragraph of sentences.</summary>
    /// <param name="said">What was done, a part at a time.</param>
    /// <returns>The paragraph.</returns>
    internal static string Sentences(IEnumerable<string> said)
    {
        ArgumentNullException.ThrowIfNull(said);

        return string.Join(
            ' ',
            said.Select(part => (part ?? string.Empty).ReplaceLineEndings(" ").Trim())
                .Where(part => part.Length > 0)
                .Select(part => part[^1] is '.' or '!' or '?' or ':' ? part : part + "."));
    }
}
