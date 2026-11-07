using System.Globalization;
using System.Text.RegularExpressions;

namespace Parley.Client.Rendering;

/// <summary>One reference to a numbered exchange in a message's text.</summary>
/// <param name="Exchange">The exchange's number.</param>
/// <param name="Start">Where its <c>#</c> starts in the text.</param>
/// <param name="Length">How long <c>#N</c> is.</param>
public sealed record ExchangeReference(long Exchange, int Start, int Length);

/// <summary>Finds the references to numbered exchanges in a message (SPEC.md §5.6).</summary>
/// <remarks>
/// <para>
/// <b>One form, and nothing else is a reference</b> (owner, 2026-11-06): <c>exchange #351</c>, or for several
/// <c>exchanges #349, #350 and #351</c>, separated by commas, "and" or "or", ignoring case. Agents are told to write it. The
/// fixed word before the <c>#</c> is what keeps ordinary numbers out: "16,000 characters", "2026", "on 351" and a bare
/// "#351" are not references.
/// </para>
/// <para>
/// <b>Display only.</b> A surface draws a reference as something the person can open. The broker never reads one, nothing is
/// addressed by one, and the number grants nothing: opening it is an operator's read of their own deployment.
/// </para>
/// </remarks>
public static partial class ExchangeReferences
{
    /// <summary>The longest number read: past it, it is not an exchange this store could hold.</summary>
    private const int Digits = 18;

    [GeneratedRegex(
        """(?<![\w#])(?:exchange\s+(?<n>#[1-9]\d{0,17})(?!\d)|exchanges\s+(?<n>#[1-9]\d{0,17})(?!\d)(?:(?:\s*,\s*(?:(?:and|or)\s+)?|\s+(?:and|or)\s+)(?<n>#[1-9]\d{0,17})(?!\d))*)(?!\w)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Form();

    /// <summary>Every reference in a text, in order.</summary>
    /// <param name="text">What somebody wrote.</param>
    /// <returns>One entry per <c>#N</c>, empty when there is none.</returns>
    public static IReadOnlyList<ExchangeReference> Find(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('#', StringComparison.Ordinal) < 0)
        {
            return [];
        }

        var found = new List<ExchangeReference>();

        try
        {
            foreach (Match match in Form().Matches(text))
            {
                foreach (Capture number in match.Groups["n"].Captures)
                {
                    if (number.Length - 1 <= Digits
                        && long.TryParse(number.ValueSpan[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    {
                        found.Add(new ExchangeReference(id, number.Index, number.Length));
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Somebody else's text: a pathological one is drawn as plain words, never a page that hangs.
            return [];
        }

        return found;
    }
}
