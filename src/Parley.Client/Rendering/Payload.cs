using System.Text.Json;

namespace Parley.Client.Rendering;

/// <summary>One numbered point in a message: an item of a Request, or its outcome in a Result.</summary>
/// <param name="Id">The label the requester gave it, such as <c>r1</c>.</param>
/// <param name="Text">The work or question, or what came of it.</param>
/// <param name="Kind">On a Request, <c>work</c> or <c>question</c>; null otherwise.</param>
/// <param name="Outcome">On a Result, how the item came out; null on a Request.</param>
/// <param name="Blocker">On a blocked outcome, the kind of blocker; null otherwise.</param>
/// <param name="Sources">On a Result, the agents it relied on; empty otherwise.</param>
public sealed record PayloadPoint(
    string Id,
    string Text,
    string? Kind,
    string? Outcome,
    string? Blocker,
    IReadOnlyList<string> Sources);

/// <summary>
/// A message's payload, read for a person rather than for a model.
/// </summary>
/// <param name="Said">The intent of a Request, or the words of a clarification. May be empty.</param>
/// <param name="Points">A Request's items, or a Result's outcomes.</param>
/// <param name="DoneWhen">What counts as finished, per rule 8.1, or null when absent.</param>
/// <param name="Raw">The document exactly as stored, for when it is not a shape this can read.</param>
/// <remarks>
/// <para>
/// SPEC.md §13 requires a payload to reach an <em>agent</em> as its stored document, never unwrapped into
/// prose, because unwrapping is how another participant's words stop looking like data. That rule is about
/// delivery into a model's context: a person reading their own deployment's log is not being asked to obey
/// it, so here the document is pulled apart and shown as a person reads. Every surface encodes each field.
/// </para>
/// <para>
/// A message written under protocol 0.4 carries <c>sub_questions</c>, <c>consequences</c> and
/// <c>resolutions</c>, and those rows were carried forward unchanged, so they are read too. Anything this
/// cannot read comes back whole in <see cref="Raw"/>: guessing at a shape the document does not have would
/// be the one way to make a transcript say something nobody said.
/// </para>
/// </remarks>
public sealed record Payload(
    string Said,
    IReadOnlyList<PayloadPoint> Points,
    string? DoneWhen,
    string? Raw)
{
    /// <summary>Reads a stored payload.</summary>
    /// <param name="json">The document as the store holds it.</param>
    /// <returns>Its parts, or the document whole when it is not a shape this knows.</returns>
    public static Payload Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Payload(string.Empty, [], null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Payload(string.Empty, [], null, json);
            }

            var said = Text(root, "intent");

            if (said.Length == 0)
            {
                said = Text(root, "text");
            }

            var points = new List<PayloadPoint>();

            Collect(root, "items", points);
            Collect(root, "outcomes", points);
            Collect(root, "sub_questions", points);
            Collect(root, "resolutions", points);

            var doneWhen = Text(root, "done_when");

            if (doneWhen.Length == 0)
            {
                doneWhen = Text(root, "consequences");
            }

            // Nothing recognisable. Shown whole rather than as an empty bubble, because a message that said
            // nothing and a message this could not read are different facts.
            return said.Length == 0 && points.Count == 0 && doneWhen.Length == 0
                ? new Payload(string.Empty, [], null, json)
                : new Payload(said, points, doneWhen.Length == 0 ? null : doneWhen, null);
        }
        catch (JsonException)
        {
            return new Payload(string.Empty, [], null, json);
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
                Optional(entry, "kind"),
                Optional(entry, "outcome") ?? Optional(entry, "resolution"),
                Optional(entry, "blocker"),
                sources));
        }
    }
}
