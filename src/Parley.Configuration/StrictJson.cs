using System.Text.Json;
using System.Text.Json.Nodes;

namespace Parley.Configuration;

/// <summary>
/// The one way Parley parses a JSON file somebody else wrote: a repository's <c>.mcp.json</c>, a
/// catalogue, a project or ports file, another program's settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>A member named twice in one object is refused at the parse.</b> <c>{"a": 1, "a": 2}</c> is what a
/// careless merge produces, and by default <c>JsonNode.Parse</c> accepts it and throws
/// <c>ArgumentException</c> later, when the object is first indexed, with the raw key in its message.
/// Nothing caught that, so one repository's file crashed the scan of all its siblings and printed the
/// key's control characters to the terminal (lens 2 run 137). <c>JsonDocument</c> accepts it silently
/// and answers whichever copy it finds, so two readers of one file could disagree on what it says.
/// </para>
/// <para>
/// <b>The refusal is a <see cref="JsonException"/> whose message names no key.</b> Every caller already
/// treats that as "this file cannot be read", and several show the message; the runtime's own message
/// for a duplicate quotes the key raw, so it is replaced. The other parse failures keep the runtime's
/// message, which gives a line and position and prints an unprintable byte as hex.
/// </para>
/// </remarks>
public static class StrictJson
{
    /// <summary>What a refused duplicate reads as, wherever it is shown.</summary>
    public const string DuplicateMessage = "An object in it names the same member twice.";

    /// <summary>Parses text into a node, refusing a duplicated member.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Any other reading options; its duplicate setting is ignored.</param>
    /// <returns>The node, or null for the JSON literal <c>null</c>.</returns>
    /// <exception cref="JsonException">The text is not JSON, or an object names a member twice.</exception>
    public static JsonNode? Parse(string text, JsonDocumentOptions options = default)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: Strict(options));
        }
        catch (JsonException refused) when (Duplicated(text, options))
        {
            throw new JsonException(DuplicateMessage, refused);
        }
    }

    /// <summary>Parses text into a document, refusing a duplicated member.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Any other reading options; its duplicate setting is ignored.</param>
    /// <returns>The document, which the caller disposes.</returns>
    /// <exception cref="JsonException">The text is not JSON, or an object names a member twice.</exception>
    public static JsonDocument Document(string text, JsonDocumentOptions options = default)
    {
        try
        {
            return JsonDocument.Parse(text, Strict(options));
        }
        catch (JsonException refused) when (Duplicated(text, options))
        {
            throw new JsonException(DuplicateMessage, refused);
        }
    }

    private static JsonDocumentOptions Strict(JsonDocumentOptions options) =>
        options with { AllowDuplicateProperties = false };

    // Told apart by parsing again with duplicates allowed rather than by the message, which is localised. Only reached
    // on a failure, so the second parse costs nothing on a good file.
    private static bool Duplicated(string text, JsonDocumentOptions options)
    {
        try
        {
            using var lenient = JsonDocument.Parse(text, options with { AllowDuplicateProperties = true });

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
