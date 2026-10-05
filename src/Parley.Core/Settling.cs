namespace Parley.Core;

/// <summary>
/// One of an agent's open questions to the person, as delivered beside the person's Request (SPEC.md §5.6).
/// </summary>
/// <param name="ExchangeId">The question's exchange, which the agent names in <c>settles</c>.</param>
/// <param name="OpenedUtc">When the agent asked it.</param>
/// <param name="Asked">The start of the last thing the agent said in it, cut to <see cref="Settling.LongestAsked"/>.</param>
public sealed record SettleableQuestion(long ExchangeId, DateTimeOffset OpenedUtc, string Asked);

/// <summary>The bounds on settling, stated once (SPEC.md §5.6, §13).</summary>
/// <remarks>
/// <b>Bounded because it crosses into a model's context</b> (principle IV). The list is the agent's own words returned to
/// it, so it needs no envelope, but twenty entries of a hundred and sixty characters is already half a message's cap,
/// charged only on a person's Request to an agent that has asked them things.
/// </remarks>
public static class Settling
{
    /// <summary>The most questions listed beside one Request; the rest are counted.</summary>
    public const int MostListed = 20;

    /// <summary>The most characters of each question listed.</summary>
    public const int LongestAsked = 160;

    /// <summary>The most exchanges one reply may name in <c>settles</c>.</summary>
    public const int MostNamed = 50;

    /// <summary>Cuts what a question said to <see cref="LongestAsked"/>, marking the cut.</summary>
    /// <param name="said">The words.</param>
    /// <returns>The words, or their start and an ellipsis.</returns>
    /// <remarks>Cut at a character boundary, never inside a surrogate pair, so the result is always valid text.</remarks>
    public static string Cut(string said)
    {
        ArgumentNullException.ThrowIfNull(said);

        var flat = string.Join(' ', said.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (flat.Length <= LongestAsked)
        {
            return flat;
        }

        var at = LongestAsked - 1;

        if (char.IsHighSurrogate(flat[at - 1]))
        {
            at--;
        }

        return string.Concat(flat.AsSpan(0, at), "…");
    }
}
