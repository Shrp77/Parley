using System.Text.Json;
using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>What kind of line this is, which decides how a surface draws it.</summary>
public enum LineKind
{
    /// <summary>The words of a message.</summary>
    /// <remarks>Zero, so an unclassified line is body text and never mistaken for a header.</remarks>
    Body = 0,

    /// <summary>Who said what to whom, when, and what became of it.</summary>
    Header = 1,

    /// <summary>One numbered sub-question, or the resolution answering it.</summary>
    Point = 2,

    /// <summary>The consequences clause of rule 8.1.</summary>
    Consequences = 3,

    /// <summary>The console's own note about who was not party to a message.</summary>
    Annotation = 4,

    /// <summary>Nothing, separating one message from the next.</summary>
    Blank = 5,
}

/// <summary>One line of a transcript.</summary>
/// <param name="Text">What it says, already indented for its kind.</param>
/// <param name="Kind">What kind of line it is.</param>
/// <param name="Colour">
/// The sender's colour slot on a header, null everywhere else. Decoration only: every header names
/// its sender in words, so a reader who sees no colour loses nothing.
/// </param>
public sealed record TranscriptLine(string Text, LineKind Kind, int? Colour);

/// <summary>
/// A conversation laid out for somebody to read, rather than run together into one blob.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The owner's word for the transcript 005 shipped was "one long ass blob
/// that is not readable by a human", and that was fair: every message came out as a header and one
/// flattened line of JSON, so twenty messages were twenty walls of braces with nothing to catch
/// the eye between them.
/// </para>
/// <para>
/// <b>Headers are distinguished structurally, not by colour.</b> A header sits at column zero and
/// nothing else does. That survives every colour being stripped, which is the property the whole
/// view is held to — a test reads the screen with attributes removed and must lose no information,
/// so any distinction carried only in colour is a distinction that test cannot see.
/// </para>
/// <para>
/// <b>Times are local here and UTC in the export.</b> A screen is read by the person in front of
/// it; a file is read by somebody else, somewhere else. <see cref="LocalTime"/> carries the
/// argument.
/// </para>
/// <para>
/// <b>Nothing an agent wrote may become structure.</b> A payload's newlines produce body lines, and
/// a body line is indented — so a peer cannot write a line that arrives at column zero and reads as
/// a header. That is the same forgery <see cref="Transcript"/> defends against in the export by
/// flattening, solved here by indenting instead, because on screen the whole point is that the
/// breaks survive.
/// </para>
/// </remarks>
public static class Reading
{
    /// <summary>How far body text is indented, in spaces.</summary>
    /// <remarks>
    /// The structural half of FR-018. Four rather than two because two is what
    /// <see cref="Transcript"/> uses for its own annotations in the export, and two conventions
    /// that look alike in two artefacts is how somebody comes to trust the wrong line.
    /// </remarks>
    public const int Indent = 4;

    /// <summary>What a destroyed conversation says instead of a transcript.</summary>
    /// <param name="destroyed">The record §14 left behind.</param>
    /// <returns>The lines, in the order they are read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destroyed"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Composed once because both surfaces have to say the same thing.</b> This is the answer to
    /// "what happened to it", and two surfaces answering it differently would be two accounts of an
    /// event that has no other account left — the record is the only one that will ever exist.
    /// </para>
    /// <para>
    /// It names what was lost and never what was said. The record holds no content, and this must
    /// not invent the impression of any.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Gone(DestroyedConversation destroyed)
    {
        ArgumentNullException.ThrowIfNull(destroyed);

        var participants = destroyed.Participants.Count == 0
            ? "nobody"
            : string.Join(", ", destroyed.Participants.Select(agent => agent.Value));

        return
        [
            $"{destroyed.Id} was destroyed on {LocalTime.Write(destroyed.DestroyedUtc)}.",
            $"It held {destroyed.Messages} message"
                + $"{(destroyed.Messages == 1 ? string.Empty : "s")}, between {participants}.",
            "Nothing of what was said was kept, and it cannot be undone.",
        ];
    }

    /// <summary>Lays out one conversation.</summary>
    /// <param name="messages">Its messages, in the order the broker recorded them.</param>
    /// <param name="exchanges">The state of every exchange those messages belong to.</param>
    /// <param name="zone">The zone to render times in, or null for the machine's own.</param>
    /// <returns>The lines, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static IReadOnlyList<TranscriptLine> Lines(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ExchangeSnapshot>? exchanges = null,
        TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var participants = Visibility.Participants(messages);
        var states = new Dictionary<long, ExchangeSnapshot>();

        foreach (var exchange in exchanges ?? [])
        {
            states[exchange.ExchangeId] = exchange;
        }

        var lines = new List<TranscriptLine>();

        foreach (var message in messages)
        {
            if (lines.Count > 0)
            {
                lines.Add(new TranscriptLine(string.Empty, LineKind.Blank, null));
            }

            lines.Add(new TranscriptLine(Header(message, states, zone), LineKind.Header,
                Palette.SlotFor(message.From)));

            foreach (var line in Body(message.PayloadJson))
            {
                lines.Add(line);
            }

            var absent = Visibility.Describe(message, participants);

            if (absent.Length > 0)
            {
                lines.Add(new TranscriptLine(Pad(absent), LineKind.Annotation, null));
            }
        }

        return lines;
    }

    /// <summary>The one line naming who said what to whom, and what became of it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="states">Exchange states, keyed by identifier.</param>
    /// <param name="zone">The zone to render the time in.</param>
    /// <returns>The header.</returns>
    private static string Header(
        Message message,
        IReadOnlyDictionary<long, ExchangeSnapshot> states,
        TimeZoneInfo? zone)
    {
        var state = message.ExchangeId is { } id && states.TryGetValue(id, out var snapshot)
            ? $", {snapshot.State}"
            : string.Empty;

        return $"{LocalTime.Write(message.CreatedUtc, zone)}  "
            + $"{message.From.Value} -> {message.To.Value}  "
            + $"({message.Type}{state})";
    }

    /// <summary>The words of a message, broken as its author wrote them.</summary>
    /// <param name="payloadJson">The payload, as the store holds it.</param>
    /// <returns>The body lines.</returns>
    /// <remarks>
    /// Reads through <see cref="Payload"/>, which already knows rule 8.1's shapes, rather than
    /// parsing again. What is added is that each part becomes lines instead of a field: an intent
    /// with paragraphs keeps them, each sub-question gets its own line, and a document this cannot
    /// read is laid out as JSON rather than run onto one.
    /// </remarks>
    private static IEnumerable<TranscriptLine> Body(string? payloadJson)
    {
        var payload = Payload.Read(payloadJson);

        if (payload.Raw is { } raw)
        {
            foreach (var line in Wrap(Pretty(raw)))
            {
                yield return new TranscriptLine(Pad(line), LineKind.Body, null);
            }

            yield break;
        }

        foreach (var line in Wrap(payload.Said))
        {
            yield return new TranscriptLine(Pad(line), LineKind.Body, null);
        }

        foreach (var point in payload.Points)
        {
            var head = string.IsNullOrEmpty(point.Resolution)
                ? $"{point.Id}: {point.Text}"
                : $"{point.Id} ({point.Resolution}): {point.Text}";

            var first = true;

            foreach (var line in Wrap(head))
            {
                yield return new TranscriptLine(
                    first ? Pad(line) : Pad("  " + line), LineKind.Point, null);
                first = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(payload.Consequences))
        {
            foreach (var line in Wrap(payload.Consequences))
            {
                yield return new TranscriptLine(Pad(line), LineKind.Consequences, null);
            }
        }
    }

    /// <summary>Splits text on the breaks its author wrote.</summary>
    /// <param name="text">The text.</param>
    /// <returns>One entry per line, with blank lines kept.</returns>
    /// <remarks>
    /// <b>Blank lines are kept, which is the whole of "paragraphs survive".</b> Dropping empty
    /// entries is the obvious tidy-up and it is what turns two paragraphs into one.
    /// </remarks>
    internal static IReadOnlyList<string> Wrap(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace("\r", "\n", StringComparison.Ordinal)
                   .Split('\n');
    }

    /// <summary>Lays a JSON document out over lines, or returns it unchanged.</summary>
    /// <param name="raw">The document.</param>
    /// <returns>The indented form, or the original when it is not JSON.</returns>
    /// <remarks>
    /// <para>
    /// Only reached for a payload <see cref="Payload"/> could not read as one of rule 8.1's shapes.
    /// Those are the documents most worth laying out, because a reader meeting one has no structure
    /// to fall back on.
    /// </para>
    /// <para>
    /// Returns the original when it will not parse rather than throwing or blanking. A payload that
    /// is not JSON should be impossible — <c>json_valid</c> is enforced on the column — and showing
    /// whatever is actually there is how somebody finds out that it was.
    /// </para>
    /// </remarks>
    internal static string Pretty(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);

            return JsonSerializer.Serialize(
                document.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>Indented, and never sorted or re-encoded beyond what indenting needs.</summary>
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    /// <summary>Indents a line so it cannot be mistaken for a header.</summary>
    /// <param name="text">The line.</param>
    /// <returns>The indented line.</returns>
    private static string Pad(string text) => new string(' ', Indent) + text;
}
