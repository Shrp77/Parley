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

    /// <summary>One numbered item of a Request, or its outcome in a Result.</summary>
    Point = 2,

    /// <summary>What a Request stored before 0.7 said counted as finished.</summary>
    DoneWhen = 3,

    /// <summary>The surface's own note about who was not party to a message.</summary>
    Annotation = 4,

    /// <summary>Nothing, separating one message from the next.</summary>
    Blank = 5,
}

/// <summary>One line of a transcript.</summary>
/// <param name="Text">What it says, already indented for its kind.</param>
/// <param name="Kind">What kind of line it is.</param>
/// <param name="Colour">
/// The sender's colour slot on a header, null everywhere else. Decoration only: every header names its
/// sender in words, so a reader who sees no colour loses nothing.
/// </param>
/// <param name="Message">
/// The message this line belongs to, or <see langword="null"/> for a line the surface wrote itself.
/// </param>
/// <remarks>
/// <para>
/// <b><paramref name="Message"/> is what lets a surface put one message in front of a reader</b> — the room's
/// "waiting on you" list names a thing owed and a person should read the question in its context rather than
/// in a list. Carried on the line rather than worked out by counting messages, because a count would be a
/// second opinion about the layout that this type already decides.
/// </para>
/// <para>
/// Null on the blank line between messages and on the record §14 leaves behind: a line the surface wrote is
/// not part of anybody's message, and answering "which message is this" with a guess would scroll a reader to
/// somewhere nothing was said.
/// </para>
/// </remarks>
public sealed record TranscriptLine(string Text, LineKind Kind, int? Colour, long? Message = null);

/// <summary>
/// A conversation laid out for somebody to read, rather than run together into one blob.
/// </summary>
/// <remarks>
/// <para>
/// <b>Headers are distinguished structurally, not by colour.</b> A header sits at column zero and nothing
/// else does, which survives every colour being stripped.
/// </para>
/// <para>
/// <b>Times are local here and UTC in the export.</b> A screen is read by the person in front of it; a file
/// is read by somebody else, somewhere else.
/// </para>
/// <para>
/// <b>Nothing a participant wrote may become structure.</b> A payload's newlines produce body lines, and a
/// body line is indented — so nobody can write a line that arrives at column zero and reads as a header.
/// </para>
/// </remarks>
public static class Reading
{
    /// <summary>How far body text is indented, in spaces.</summary>
    public const int Indent = 4;

    /// <summary>What a destroyed conversation says instead of a transcript.</summary>
    /// <param name="destroyed">The record §14 left behind.</param>
    /// <returns>The lines, in the order they are read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destroyed"/> is null.</exception>
    /// <remarks>Composed once, because this is the only account of the event that will ever exist.</remarks>
    public static IReadOnlyList<string> Gone(DestroyedConversation destroyed)
    {
        ArgumentNullException.ThrowIfNull(destroyed);

        var participants = destroyed.Participants.Count == 0
            ? "nobody"
            : string.Join(", ", destroyed.Participants.Select(agent => agent.Value));

        return
        [
            $"{destroyed.Id} was destroyed on {LocalTime.Write(destroyed.DestroyedUtc)}.",
            $"It held {destroyed.Messages} message{(destroyed.Messages == 1 ? string.Empty : "s")}, between {participants}.",
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

            lines.Add(new TranscriptLine(
                Header(message, states, zone),
                LineKind.Header,
                Palette.SlotFor(message.From),
                message.Id));

            foreach (var line in Body(message.PayloadJson))
            {
                lines.Add(line with { Message = message.Id });
            }

            var absent = Visibility.Describe(message, participants);

            if (absent.Length > 0)
            {
                lines.Add(new TranscriptLine(Pad(absent), LineKind.Annotation, null, message.Id));
            }
        }

        return lines;
    }

    /// <summary>The state of an exchange, as a header says it.</summary>
    /// <param name="snapshot">The exchange.</param>
    /// <returns>A short phrase: open, or how it closed.</returns>
    /// <remarks>
    /// The outcome is named because a closed exchange that was finished and one the person ended are
    /// opposite facts, and a transcript that said only "Closed" for both would hide the one that matters.
    /// </remarks>
    public static string State(ExchangeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.State == ExchangeState.Open
            ? "open"
            : snapshot.Outcome switch
            {
                ExchangeOutcome.Complete => "closed, complete",
                ExchangeOutcome.Blocked => "closed, blocked",
                ExchangeOutcome.Closed => "closed before it finished",
                _ => "closed"
            };
    }

    private static string Header(Message message, IReadOnlyDictionary<long, ExchangeSnapshot> states, TimeZoneInfo? zone)
    {
        var state = states.TryGetValue(message.ExchangeId, out var snapshot) ? $", {State(snapshot)}" : string.Empty;

        return $"{LocalTime.Write(message.CreatedUtc, zone)}  "
            + $"{message.From.Value} -> {message.To.Value}  "
            + $"({message.Type}{state}, {Delivery.Of(message, zone)})";
    }

    /// <summary>The words of a message, broken as its author wrote them.</summary>
    /// <param name="payloadJson">The payload, as the store holds it.</param>
    /// <returns>The body lines.</returns>
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
            var first = true;

            foreach (var line in Wrap(Point(point)))
            {
                yield return new TranscriptLine(first ? Pad(line) : Pad("  " + line), LineKind.Point, null);
                first = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(payload.DoneWhen))
        {
            foreach (var line in Wrap("Done when: " + payload.DoneWhen))
            {
                yield return new TranscriptLine(Pad(line), LineKind.DoneWhen, null);
            }
        }
    }

    /// <summary>One item or outcome as a line of text.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Its id, what kind or outcome it is, and its words.</returns>
    public static string Point(PayloadPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        var tag = point.Outcome switch
        {
            null => point.Kind,
            "blocked" when point.Blocker is { } blocker => $"blocked: {blocker.Replace('_', ' ')}",
            var outcome => outcome
        };

        var head = string.IsNullOrEmpty(tag) ? $"{point.Id}: {point.Text}" : $"{point.Id} ({tag}): {point.Text}";

        return point.Sources.Count == 0 ? head : $"{head} [relied on {string.Join(", ", point.Sources)}]";
    }

    /// <summary>Splits text on the breaks its author wrote.</summary>
    /// <param name="text">The text.</param>
    /// <returns>One entry per line, with blank lines kept.</returns>
    /// <remarks><b>Blank lines are kept, which is the whole of "paragraphs survive".</b></remarks>
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
    /// Only reached for a payload <see cref="Payload"/> could not read. Returns the original when it will
    /// not parse, because showing whatever is actually there is how somebody finds out that it was.
    /// </remarks>
    internal static string Pretty(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private static string Pad(string text) => new string(' ', Indent) + text;
}
