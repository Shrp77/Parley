using System.Text.Json;

namespace Parley.Core;

/// <summary>
/// Reads the parts of a Request or Result payload the protocol itself depends on (SPEC.md §5.1, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// The payload is otherwise opaque to the broker, which delivers it as the document it is (§13). Two
/// facts about it are protocol rather than content: which items a Request carried, and what outcome a
/// Result gave each of them — because a Result must answer every item and nothing else, and the
/// exchange's rolled-up outcome is derived from it rather than taken on the sender's word.
/// </para>
/// <para>
/// A Request written under protocol 0.4 carried <c>sub_questions</c> rather than <c>items</c>. Those rows
/// were copied forward unchanged, so their items are read from there.
/// </para>
/// </remarks>
public static class PayloadShape
{
    /// <summary>The one item a Result names when the Request's own items cannot be read.</summary>
    /// <remarks>A Result must carry at least one outcome, so such a Request is answered as a whole.</remarks>
    public const string WholeRequest = "request";

    /// <summary>The item identifiers a Request carried that a Result can answer, in order.</summary>
    /// <param name="requestJson">The Request payload, or <see langword="null"/>.</param>
    /// <returns>The identifiers; empty when there are none or the payload cannot be read.</returns>
    /// <remarks>
    /// <b>An id longer than <see cref="MessageLimits.LongestItemId"/> is left out.</b> A Request is refused one, but
    /// 0.4 bounded sub-question ids only by being distinct, and migration carries its open questions forward. Read
    /// as an item, such an id made the work unanswerable for ever — a Result echoing it was refused by storage and
    /// one leaving it out was refused for not matching — and nothing in 0.5 times out. Found by the review gate.
    /// </remarks>
    public static IReadOnlyList<string> ItemIds(string? requestJson)
    {
        if (string.IsNullOrEmpty(requestJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(requestJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            foreach (var name in new[] { "items", "sub_questions" })
            {
                if (document.RootElement.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    return
                    [
                        .. list.EnumerateArray()
                            .Select(item => item.ValueKind == JsonValueKind.Object
                                            && item.TryGetProperty("id", out var id)
                                            && id.ValueKind == JsonValueKind.String
                                ? id.GetString()
                                : null)
                            .Where(id => !string.IsNullOrEmpty(id) && id.Length <= MessageLimits.LongestItemId)
                            .Select(id => id!)
                    ];
                }
            }
        }
        catch (JsonException)
        {
            // Storage holds every payload to json_valid, so this is reached only by a caller passing
            // something that was never stored. Nothing readable is the honest answer.
        }

        return [];
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
