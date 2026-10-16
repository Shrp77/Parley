using System.Text.Json;

namespace Parley.Client.Rendering;

/// <summary>One outcome of a Result.</summary>
/// <param name="Id">The item it answers, such as <c>request</c>.</param>
/// <param name="Text">What came of it.</param>
/// <param name="Outcome">How the item came out.</param>
/// <param name="Blocker">On a blocked outcome, the kind of blocker; null otherwise.</param>
/// <param name="Sources">The agents it relied on; empty otherwise.</param>
public sealed record PayloadPoint(
    string Id,
    string Text,
    string? Outcome,
    string? Blocker,
    IReadOnlyList<string> Sources);

/// <summary>
/// A message's payload, read for a person rather than for a model.
/// </summary>
/// <param name="Said">The ask of a Request, or the words of a clarification. May be empty.</param>
/// <param name="Points">A Result's outcomes.</param>
/// <param name="Raw">The document exactly as stored, for when it is not a shape this can read.</param>
/// <remarks>
/// <para>
/// SPEC.md §13 requires a payload to reach an <em>agent</em> as its stored document, never unwrapped into
/// prose, because unwrapping is how another participant's words stop looking like data. That rule is about
/// delivery into a model's context: a person reading their own deployment's log is not being asked to obey
/// it, so here the document is pulled apart and shown as a person reads. Every surface encodes each field.
/// </para>
/// <para>
/// Anything this cannot read comes back whole in <see cref="Raw"/>: guessing at a shape the document does not
/// have would be the one way to make a transcript say something nobody said.
/// </para>
/// </remarks>
public sealed record Payload(
    string Said,
    IReadOnlyList<PayloadPoint> Points,
    string? Raw)
{
    /// <summary>Reads a stored payload.</summary>
    /// <param name="json">The document as the store holds it.</param>
    /// <returns>Its parts, or the document whole when it is not a shape this knows.</returns>
    public static Payload Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Payload(string.Empty, [], null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Payload(string.Empty, [], json);
            }

            // A Request carries `ask` (§5.1) and a clarification `text`; a Result carries `outcomes`.
            var said = Text(root, "ask");

            if (said.Length == 0)
            {
                said = Text(root, "text");
            }

            var points = new List<PayloadPoint>();

            Collect(root, "outcomes", points);

            // Nothing recognisable. Shown whole rather than as an empty bubble, because a message that said
            // nothing and a message this could not read are different facts.
            return said.Length == 0 && points.Count == 0
                ? new Payload(string.Empty, [], json)
                : new Payload(said, points, null);
        }
        catch (JsonException)
        {
            return new Payload(string.Empty, [], json);
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() ?? string.Empty
            : string.Empty;

    private static string? Optional(JsonElement root, string name) =>
        Text(root, name) is { Length: > 0 } present ? present : null;

    private static void Collect(JsonElement root, string name, List<PayloadPoint> into)
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

            var sources = entry.TryGetProperty("sources", out var listed)
                ? listed.ValueKind switch
                {
                    JsonValueKind.Array => [.. listed.EnumerateArray()
                        .Where(s => s.ValueKind == JsonValueKind.String)
                        .Select(s => s.GetString() ?? string.Empty)
                        .Where(s => s.Length > 0)],
                    JsonValueKind.String => [listed.GetString() ?? string.Empty],
                    _ => (IReadOnlyList<string>)[]
                }
                : [];

            into.Add(new PayloadPoint(
                Text(entry, "id"),
                Text(entry, "text"),
                Optional(entry, "outcome"),
                Optional(entry, "blocker"),
                sources));
        }
    }
}
