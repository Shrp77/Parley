using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// The payload the broker sends an asker when nobody ever answered.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §14: ghosting does not discard. It hands the question back to the asker with the
/// asker's own stated consequences, which is what makes the notice actionable rather than merely
/// informative — the asker wrote down what it would do for each outcome, so the reply can say
/// what it had planned and leave it to decide.
/// </para>
/// <para>
/// Reconstituting the question is the broker's job for the reason §4.2 gives about answers, only
/// more so. Hours have passed, the asker has done unrelated work and may have been compacted or
/// restarted, and an exchange identifier alone is worthless to a model that no longer remembers
/// asking. A bare "exchange 47 was ghosted" is a notification nobody can act on.
/// </para>
/// </remarks>
internal static class GhostNotice
{
    /// <summary>
    /// Builds the notice for a question that ran out of time.
    /// </summary>
    /// <param name="answerer">The agent that never answered.</param>
    /// <param name="questionPayloadJson">The original question, or <see langword="null"/>.</param>
    /// <returns>The payload to send, within the size cap of SPEC.md rule 8.5.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="answerer"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The question's own fields are carried back rather than summarised, so the asker reads what
    /// it wrote. They are also dropped one at a time if the result would not fit: the cap in rule 8.5
    /// is a schema constraint and a notice that cannot be stored is a notice nobody receives,
    /// which is strictly worse than a short one.
    /// </para>
    /// <para>
    /// The order they are dropped in is the order of increasing usefulness. <c>sub_questions</c>
    /// goes first, being the longest and the part the asker can most easily reconstruct from its
    /// own intent; <c>intent</c> second; <c>consequences</c> last, because §14 singles it out as
    /// the field that makes this actionable. What never drops is the statement that nobody
    /// answered, which is the message itself.
    /// </para>
    /// </remarks>
    internal static string For(AgentName answerer, string? questionPayloadJson)
    {
        var notice = new JsonObject
        {
            ["ghosted"] = true,
            ["text"] =
                $"No answer came from {answerer.Value}, and none is coming now — the exchange " +
                "ran out of time and the broker has closed it. Nothing is waiting on you. If " +
                "this still matters, ask again; if it does not, the plan you recorded below is " +
                "what you were going to do."
        };

        var question = Parse(questionPayloadJson);

        if (question is null)
        {
            return notice.ToJsonString();
        }

        foreach (var field in new[] { "consequences", "intent", "sub_questions" })
        {
            if (question.TryGetPropertyValue(field, out var value) && value is not null)
            {
                notice[field] = value.DeepClone();
            }
        }

        // Trimmed to fit, dropping the least useful first. Measured rather than estimated: the
        // stored form is the JSON, and escaping makes a character an agent typed cost more than
        // one here.
        foreach (var field in new[] { "sub_questions", "intent" })
        {
            if (notice.ToJsonString().Length <= MessageLimits.MaxPayloadCharacters)
            {
                break;
            }

            notice.Remove(field);
        }

        return notice.ToJsonString();
    }

    /// <summary>Reads a stored question payload, if it is one.</summary>
    /// <param name="payloadJson">The stored payload.</param>
    /// <returns>The parsed object, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Returns nothing rather than throwing on a payload it cannot read. The schema checks that
    /// stored payloads are well formed, so this is unreachable — and a sweep that threw here
    /// would stop ghosting every other exchange in the same pass, which is a worse outcome than
    /// a notice that carries only the news.
    /// </remarks>
    private static JsonObject? Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payloadJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
