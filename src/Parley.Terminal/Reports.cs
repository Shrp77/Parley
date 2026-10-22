namespace Parley.Terminal;

/// <summary>
/// What a screen that did several things says about them on the message line.
/// </summary>
/// <remarks>
/// <para>
/// <b>One paragraph of sentences, never lines.</b> The message line draws every control character as
/// <c>�</c> on purpose — a line break in a broker's text could forge a second line, so
/// <see cref="Shell.Said"/> makes one visible rather than obeying it. The line wraps on its own; what separates
/// the parts is punctuation, which nothing can mistake for structure.
/// </para>
/// <para>
/// A type of its own because several screens build the same report.
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
