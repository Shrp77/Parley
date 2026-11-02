namespace Parley.Client;

/// <summary>An agent's words carried inside a sentence of the person's own, shortened so they cannot crowd it out.</summary>
/// <remarks>
/// Roadmap text and ownership lines have no bound, and a terminal dialog that cannot scroll cuts its tail, so an agent's
/// words drawn whole push what the sentence says, and what a choice does, off the screen.
/// </remarks>
public static class AgentWords
{
    /// <summary>How many characters of an agent's words a sentence of the person's own carries.</summary>
    public const int ShortLength = 80;

    /// <summary>The words, cut to <see cref="ShortLength"/> characters with an ellipsis, never inside a pair.</summary>
    /// <param name="text">The words.</param>
    /// <returns>The words, or as many as fit and an ellipsis. The caller adds any quotation marks.</returns>
    public static string Shortened(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= ShortLength)
        {
            return text;
        }

        var cut = ShortLength - 1;

        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut), "…");
    }
}
