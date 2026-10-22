using System.Text.Json;

namespace Parley.Core;

/// <summary>
/// Reads the parts of a Request or Result payload the protocol itself depends on (SPEC.md §5.1, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// The payload is otherwise opaque to the broker, which delivers it as the document it is (§13). One
/// fact about it is protocol rather than content: what outcome a Result gave the Request — because the
/// exchange's rolled-up outcome is derived from it rather than taken on the sender's word.
/// </para>
/// </remarks>
public static class PayloadShape
{
    /// <summary>The one item a Result names: the Request, answered as a whole (SPEC.md §5.1).</summary>
    /// <remarks>A Request declares no identifiers, and a Result must carry at least one outcome.</remarks>
    public const string WholeRequest = "request";

    /// <summary>What a message says, in the words its sender wrote.</summary>
    /// <param name="payloadJson">A Request, Clarify or Result payload, or <see langword="null"/>.</param>
    /// <returns>The words, trimmed; empty when there are none or the payload cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// A Request carries its words in <c>ask</c> and a Clarify in <c>text</c>; a Result carries them against its
    /// outcomes, joined here with a space in the order written. The facilitator reads an agent's answer with this.
    /// </para>
    /// <para>
    /// <b>In Core because the broker is what reads it.</b> What an agent said decides whether its answer becomes a line,
    /// and a broker that parsed it differently from the surface a person reviews it on would accept one thing and show
    /// another.
    /// </para>
    /// </remarks>
    public static string Spoken(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var field in (ReadOnlySpan<string>)["ask", "text"])
            {
                if (root.TryGetProperty(field, out var said) && said.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(said.GetString()))
                {
                    return said.GetString()!.Trim();
                }
            }

            if (!root.TryGetProperty("outcomes", out var outcomes) || outcomes.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var words = outcomes.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.Object)
                .Select(o => o.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null)
                .Where(text => !string.IsNullOrWhiteSpace(text));

            return string.Join(" ", words).Trim();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reads the outcome a Result gave each item.</summary>
    /// <param name="resultJson">The Result payload.</param>
    /// <param name="outcomes">The item identifiers and outcomes, when the payload is well formed.</param>
    /// <returns>
    /// <see langword="true"/> if the payload has an <c>outcomes</c> array whose every entry names an id and
    /// an outcome the protocol defines.
    /// </returns>
    public static bool TryReadOutcomes(string resultJson, out IReadOnlyList<(string Id, Outcome Outcome)> outcomes)
    {
        outcomes = [];

        try
        {
            using var document = JsonDocument.Parse(resultJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("outcomes", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var read = new List<(string, Outcome)>();

            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("outcome", out var word) || word.ValueKind != JsonValueKind.String
                    || !OutcomeTokens.TryParse(word.GetString(), out Outcome outcome))
                {
                    return false;
                }

                read.Add((id.GetString()!, outcome));
            }

            outcomes = read;
            return read.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
