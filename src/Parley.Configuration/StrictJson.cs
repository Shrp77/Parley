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

    /// <summary>The most bytes a file read through this class may hold.</summary>
    /// <remarks>
    /// <para>
    /// Only JSON is read this way: a repository's launch configuration, a few hundred bytes for each project it answers
    /// in; a node's catalogue, a few hundred bytes for each agent on one machine; the terminal's addressing file, a few
    /// names for each conversation a person has written in; and Dropbox's account file, a few paths. A mebibyte is
    /// thousands of times any of them, so no file anybody wrote by hand or by a tool reaches it. Without a bound a
    /// repository's <c>.mcp.json</c> past about a billion characters threw <see cref="OutOfMemoryException"/>, which no
    /// reader catches, out of the node's catalogue scan, and ended the node with every resident agent on the machine
    /// (lens 2 run 139). Anybody who can write in a repository can write that file, an agent working in it included.
    /// </para>
    /// <para>
    /// A repository's instructions file is not read this way. It is somebody's prose, of any length, and enrolment only
    /// appends to it, so a bound refused a long one and stopped the enrolment (lens 3 run 147).
    /// </para>
    /// </remarks>
    public const int MostBytes = 1 << 20;

    /// <summary>What a file refused for its size reads as, wherever it is shown.</summary>
    public const string TooLargeMessage = "It holds more than 1 MiB, which is more than Parley reads from one file.";

    /// <summary>Reads a file's text, refusing one larger than <see cref="MostBytes"/> without reading it.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its text, decoded as <see cref="File.ReadAllText(string)"/> decodes it.</returns>
    /// <exception cref="JsonException">The file is larger than <see cref="MostBytes"/>.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static string ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        return Read(stream);
    }

    /// <summary>Reads a file's text, refusing one larger than <see cref="MostBytes"/> without reading it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Its text, decoded as <see cref="File.ReadAllText(string)"/> decodes it.</returns>
    /// <exception cref="JsonException">The file is larger than <see cref="MostBytes"/>.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a stream's text from where it stands, refusing more than <see cref="MostBytes"/>.</summary>
    /// <param name="stream">The stream, left open.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="JsonException">More than <see cref="MostBytes"/> remain.</exception>
    /// <remarks>
    /// The length is checked before anything is read, and the read stops at the bound too, so a file that grows after
    /// the check is refused rather than read whole.
    /// </remarks>
    public static string Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = Bounded(stream);
        var chunk = new byte[81920];
        int read;

        while ((read = stream.Read(chunk)) > 0)
        {
            Append(buffer, chunk, read);
        }

        return Decoded(buffer);
    }

    /// <summary>Reads a stream's text from where it stands, refusing more than <see cref="MostBytes"/>.</summary>
    /// <param name="stream">The stream, left open.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="JsonException">More than <see cref="MostBytes"/> remain.</exception>
    /// <remarks>
    /// <para>
    /// The length is checked before anything is read, and the read stops at the bound too, so a file that grows after
    /// the check is refused rather than read whole.
    /// </para>
    /// <para>
    /// The loop is <see cref="Read"/>'s, written again because one is synchronous and the other is not: shared, one of
    /// them would block on the other's task. What each step does is shared, so the bound is checked in one place.
    /// </para>
    /// </remarks>
    public static async Task<string> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = Bounded(stream);
        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            Append(buffer, chunk, read);
        }

        return Decoded(buffer);
    }

    // Refused on the length alone when the stream has one, so a file of a billion characters is never read at all.
    private static MemoryStream Bounded(Stream stream)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MostBytes)
        {
            throw new JsonException(TooLargeMessage);
        }

        return new MemoryStream();
    }

    private static void Append(MemoryStream buffer, byte[] chunk, int read)
    {
        if (buffer.Length + read > MostBytes)
        {
            throw new JsonException(TooLargeMessage);
        }

        buffer.Write(chunk, 0, read);
    }

    // UTF-8 unless a byte-order mark says otherwise, which is what File.ReadAllText does.
    private static string Decoded(MemoryStream buffer)
    {
        buffer.Position = 0;

        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

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
