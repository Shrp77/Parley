using System.Text;
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
/// <b>So is a name or string that is not valid text</b>: a lone surrogate escape such as <c>"T\uD800"</c>, which
/// JavaScript's <c>JSON.stringify</c> writes for a string it cut in half. Checking for a duplicate makes the runtime
/// decode every member name at the parse, and a lone surrogate there throws
/// <see cref="InvalidOperationException"/>, which no caller catches, even in an object nobody reads (lens 1 run
/// 132). The same text in a value throws the same exception later, when a caller reads it. It is refused rather than
/// read leniently because that keeps one rule with the duplicate: what this returns can be read throughout without
/// throwing, and a file that cannot be is unreadable, reported, and its siblings unaffected. Read leniently, every
/// caller would have to guard every read, which is the latent crash this class exists to remove. The readers rely on
/// it: <see cref="LaunchConfiguration"/> walks server names and reads the agent's name with no guard of its own, and
/// before this a lone surrogate in a value threw out of that read, through the node's catalogue scan, and ended the
/// node with every resident agent on the machine (lens 2 run 138).
/// </para>
/// <para>
/// <b>A refusal is a <see cref="JsonException"/> whose message names no key.</b> Every caller already
/// treats that as "this file cannot be read", and several show the message; the runtime's own message
/// for a duplicate quotes the key raw, so it is replaced. The other parse failures keep the runtime's
/// message, which gives a line and position and prints an unprintable byte as hex.
/// </para>
/// </remarks>
public static class StrictJson
{
    /// <summary>What a refused duplicate reads as, wherever it is shown.</summary>
    public const string DuplicateMessage = "An object in it names the same member twice.";

    /// <summary>What a refused name or string that is not valid text reads as, wherever it is shown.</summary>
    public const string BrokenTextMessage = "A name or string in it is not valid Unicode text.";

    /// <summary>Parses text into a node, refusing a duplicated member and text that does not decode.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Any other reading options; its duplicate setting is ignored.</param>
    /// <returns>The node, or null for the JSON literal <c>null</c>.</returns>
    /// <exception cref="JsonException">
    /// The text is not JSON, an object names a member twice, or a name or string is not valid text.
    /// </exception>
    public static JsonNode? Parse(string text, JsonDocumentOptions options = default)
    {
        var node = Refusing(text, options, () => JsonNode.Parse(text, documentOptions: Strict(options)));

        EveryStringDecodes(text, options);

        return node;
    }

    /// <summary>Parses text into a document, refusing a duplicated member and text that does not decode.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Any other reading options; its duplicate setting is ignored.</param>
    /// <returns>The document, which the caller disposes.</returns>
    /// <exception cref="JsonException">
    /// The text is not JSON, an object names a member twice, or a name or string is not valid text.
    /// </exception>
    public static JsonDocument Document(string text, JsonDocumentOptions options = default)
    {
        var document = Refusing(text, options, () => JsonDocument.Parse(text, Strict(options)));

        try
        {
            EveryStringDecodes(text, options);

            return document;
        }
        catch
        {
            document.Dispose();

            throw;
        }
    }

    private static JsonDocumentOptions Strict(JsonDocumentOptions options) =>
        options with { AllowDuplicateProperties = false };

    // Every failure the strict parse raises leaves as a JsonException, so a caller's ordinary "unreadable" path takes
    // it. ArgumentException is a lone surrogate character in the text itself, met when it is transcoded; a null text
    // is the caller's mistake and stays one.
    private static T Refusing<T>(string text, JsonDocumentOptions options, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (JsonException refused) when (Duplicated(text, options))
        {
            throw new JsonException(DuplicateMessage, refused);
        }
        catch (InvalidOperationException broken)
        {
            throw new JsonException(BrokenTextMessage, broken);
        }
        catch (ArgumentException broken) when (broken is not ArgumentNullException)
        {
            throw new JsonException(BrokenTextMessage, broken);
        }
    }

    // The strict parse decodes member names but not string values, which the runtime decodes when they are read. One
    // pass over text already parsed decodes both, so nothing a caller reads later can fail to.
    private static void EveryStringDecodes(string text, JsonDocumentOptions options)
    {
        var reader = new Utf8JsonReader(
            Encoding.UTF8.GetBytes(text),
            new JsonReaderOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                CommentHandling = options.CommentHandling,
                MaxDepth = options.MaxDepth,
            });

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
                {
                    _ = reader.GetString();
                }
            }
        }
        catch (InvalidOperationException broken)
        {
            throw new JsonException(BrokenTextMessage, broken);
        }
    }

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
