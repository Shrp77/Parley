using System.Text.Json;

namespace Parley.Web.Rendering;

/// <summary>One numbered point in a message: a sub-question, or its resolution.</summary>
/// <param name="Id">The label the asker gave it, such as <c>q1</c>.</param>
/// <param name="Text">The question, or the answer to it.</param>
/// <param name="Resolution">How it was resolved, on an answer; null on a question.</param>
public sealed record PayloadPoint(string Id, string Text, string? Resolution);

/// <summary>
/// A message's payload, read for a person rather than for a model.
/// </summary>
/// <param name="Said">The intent of a question, or the words of a notice. May be empty.</param>
/// <param name="Points">Its sub-questions, or the resolutions answering them.</param>
/// <param name="Consequences">What turns on the answer, per rule 8.1, or null when absent.</param>
/// <param name="Ghosted">Whether this is the broker reporting an abandoned exchange.</param>
/// <param name="Raw">
/// The document exactly as stored, for when it is not a shape this can read.
/// </param>
/// <remarks>
/// <para>
/// SPEC.md §13 requires a payload to reach an <em>agent</em> as its stored JSON document, never
/// unwrapped into prose, because unwrapping is how a peer's words stop looking like data. That
/// rule is about delivery into another agent's context and does not govern this: there is no
/// model here, and an operator reading their own deployment's log is not being asked to obey it.
/// </para>
/// <para>
/// So the document is pulled apart and shown as a person reads. Razor encodes every field, which
/// is what keeps a peer's text content rather than markup — the same protection the raw rendering
/// had, since the danger was never the braces.
/// </para>
/// <para>
/// The shapes are rule 8.1's: a question carries an intent, numbered sub-questions and the
/// consequences of the answer; an answer carries a resolution per sub-question; a notice carries
/// text. Read from a real deployment rather than guessed — an earlier version of this looked for
/// <c>question</c> and <c>answer</c> and found neither, so every message rendered as a row of
/// JSON.
/// </para>
/// <para>
/// Anything it cannot read comes back whole in <see cref="Raw"/> and is shown as stored. Guessing
/// at a shape the document does not have would be the one way to make a transcript say something
/// the agent did not.
/// </para>
/// </remarks>
public sealed record Payload(
    string Said,
    IReadOnlyList<PayloadPoint> Points,
    string? Consequences,
    bool Ghosted,
    string? Raw)
{
    /// <summary>Reads a stored payload.</summary>
    /// <param name="json">The document as the store holds it.</param>
    /// <returns>Its parts, or the document whole when it is not a shape this knows.</returns>
    public static Payload Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Payload(string.Empty, [], null, false, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Payload(string.Empty, [], null, false, json);
            }

            var said = Text(root, "intent");

            if (said.Length == 0)
            {
                said = Text(root, "text");
            }

            var points = new List<PayloadPoint>();

            Collect(root, "sub_questions", "text", null, points);
            Collect(root, "resolutions", "text", "resolution", points);

            var consequences = Text(root, "consequences");
            var ghosted = root.TryGetProperty("ghosted", out var flag)
                && flag.ValueKind == JsonValueKind.True;

            // Nothing recognisable. Shown whole rather than as an empty bubble, because a message
            // that said nothing and a message this could not read are different facts.
            return said.Length == 0 && points.Count == 0 && consequences.Length == 0
                ? new Payload(string.Empty, [], null, false, json)
                : new Payload(
                    said,
                    points,
                    consequences.Length == 0 ? null : consequences,
                    ghosted,
                    null);
        }
        catch (JsonException)
        {
            // The column carries json_valid, so this should not happen -- and if it ever does,
            // showing the bytes is the honest answer rather than an empty message.
            return new Payload(string.Empty, [], null, false, json);
        }
    }

    /// <summary>Reads one string field, or empty when it is absent or not a string.</summary>
    /// <param name="root">The payload object.</param>
    /// <param name="name">The field to read.</param>
    /// <returns>Its value, or an empty string.</returns>
    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Reads a numbered array of points into the list.</summary>
    /// <param name="root">The payload object.</param>
    /// <param name="name">The array field to read.</param>
    /// <param name="text">Which field of each entry carries its words.</param>
    /// <param name="resolution">Which field carries how it was resolved, if any.</param>
    /// <param name="into">Where to put them.</param>
    private static void Collect(
        JsonElement root,
        string name,
        string text,
        string? resolution,
        List<PayloadPoint> into)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            into.Add(new PayloadPoint(
                Text(entry, "id"),
                Text(entry, text),
                resolution is null ? null : Text(entry, resolution)));
        }
    }
}
