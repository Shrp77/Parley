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

    /// <summary>One outcome of a Result.</summary>
    Point = 2,

    /// <summary>The surface's own note about who was not party to a message.</summary>
    Annotation = 3,

    /// <summary>Nothing, separating one message from the next.</summary>
    Blank = 4,
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
    /// <param name="zone">The zone to render times in, or null for the machine's own.</param>
    /// <returns>The lines, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>A header is a time, a sender and a recipient, and nothing else.</b> Humans just talk; a header does
    /// not begin by saying "Inquiry" and "Response".
    /// </para>
    /// <para>
    /// <b>The facts left out are carried elsewhere.</b> The type is derivable from the conversation. The
    /// exchange's state is what the room column says per peer, in the form somebody can act on — "waiting on
    /// you", "owes you". <i>Collected</i> would mislead: it means the recipient drained its inbox, which for a
    /// person is a key they press. The export still records all of it (see <see cref="Transcript"/>), which is
    /// where a fact for somebody else, somewhere else, belongs.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TranscriptLine> Lines(
        IReadOnlyList<Message> messages,
        TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var lines = new List<TranscriptLine>();

        foreach (var message in messages)
        {
            if (lines.Count > 0)
            {
                lines.Add(new TranscriptLine(string.Empty, LineKind.Blank, null));
            }

            lines.Add(new TranscriptLine(
                Header(message, zone),
                LineKind.Header,
                Palette.SlotFor(message.From),
                message.Id));

            foreach (var line in Body(message.PayloadJson))
            {
                lines.Add(line with { Message = message.Id });
            }
        }

        return lines;
    }

    /// <summary>Lays a conversation out for reading, with what each reply settled beneath it (SPEC.md §5.6).</summary>
    /// <param name="export">The conversation: its messages and the state of its exchanges.</param>
    /// <param name="zone">The zone to render times in, or <see langword="null"/> for this machine's.</param>
    /// <returns>The transcript, line by line.</returns>
    /// <remarks>
    /// <b>From the record, never from the reply's words.</b> The person must see what an agent decided its words settled,
    /// and the broker cannot check that a model said so — so the list is drawn from the exchanges the reply closed, the
    /// way <see cref="Settled"/> finds them, beneath the reply that closed them.
    /// </remarks>
    public static IReadOnlyList<TranscriptLine> Lines(ConversationExport export, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(export);

        var lines = new List<TranscriptLine>();

        foreach (var line in Lines(export.Messages, zone))
        {
            lines.Add(line);

            if (line.Kind != LineKind.Header || line.Message is not { } id)
            {
                continue;
            }

            var message = export.Messages.First(m => m.Id == id);

            if (Settled(message, export) is { Count: > 0 } settled)
            {
                lines.Add(new TranscriptLine(Pad(SettledLine(settled)), LineKind.Annotation, null, id));
            }

            if (RoleRequest(message, export) is { } role)
            {
                lines.Add(new TranscriptLine(Pad(role), LineKind.Annotation, null, id));
            }
        }

        return lines;
    }

    /// <summary>Where a Request to a role stands, beneath the first of its Requests in the conversation (SPEC.md §5.5).</summary>
    /// <param name="message">A message in the conversation.</param>
    /// <param name="export">The conversation.</param>
    /// <returns>The sentence, or <see langword="null"/> for any message but the first Request of a Request to a role.</returns>
    /// <remarks>
    /// Counted from how each member's exchange closed, not from the messages present, so a Result whose words retention
    /// pruned still counts as an answer. Said once per Request, beneath its first member, since every member's Request
    /// carries the same words.
    /// </remarks>
    public static string? RoleRequest(Message message, ConversationExport export)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(export);

        if (message.Type != MessageType.Request
            || export.Exchanges.FirstOrDefault(e => e.ExchangeId == message.ExchangeId)?.RequestGroup is not { } group)
        {
            return null;
        }

        var members = export.Exchanges.Where(e => e.RequestGroup == group).Select(e => e.ExchangeId).ToHashSet();
        var first = export.Messages.FirstOrDefault(m => m.Type == MessageType.Request && members.Contains(m.ExchangeId));

        if (first?.Id != message.Id)
        {
            return null;
        }

        var answered = export.Exchanges.Count(e => members.Contains(e.ExchangeId)
                                                    && e.Outcome is ExchangeOutcome.Complete or ExchangeOutcome.Blocked);

        return RoleRequestLine(message.To.Role, members.Count, answered);
    }

    /// <summary>A Request to a role's standing, as one sentence.</summary>
    /// <param name="role">The role it went to.</param>
    /// <param name="asked">How many agents it reached.</param>
    /// <param name="answered">How many have answered.</param>
    /// <returns>The sentence.</returns>
    public static string RoleRequestLine(string role, int asked, int answered) =>
        $"Sent to every agent in role {role} ({asked}): {answered} of {asked} answered.";

    /// <summary>What a reply settled, as one sentence beneath it.</summary>
    /// <param name="settled">The start of each question settled.</param>
    /// <returns>The sentence.</returns>
    public static string SettledLine(IReadOnlyList<string> settled)
    {
        ArgumentNullException.ThrowIfNull(settled);

        return "Settled: " + string.Join(", ", settled.Select(asked => "\u201C" + asked + "\u201D"));
    }

    /// <summary>The questions a reply settled, each as the start of what was asked (SPEC.md §5.6).</summary>
    /// <param name="message">The reply.</param>
    /// <param name="export">The conversation it is in.</param>
    /// <returns>The start of each question it settled, oldest first; empty for any other message.</returns>
    /// <remarks>
    /// A question was settled by this reply when it names the reply's exchange as what settled it, closed at the reply's
    /// own instant, and was asked by the reply's sender. The broker writes the reply and the settle in one transaction at
    /// one instant, and storage refuses a settle no reply stands beside, so the match is exact rather than a guess.
    /// Where retention has pruned a question's own words, the exchange is named instead.
    /// </remarks>
    public static IReadOnlyList<string> Settled(Message message, ConversationExport export)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(export);

        if (message.Type == MessageType.Request)
        {
            return [];
        }

        return [.. export.Exchanges
            .Where(e => e.Outcome == ExchangeOutcome.Settled
                && e.SettledBy == message.ExchangeId
                && e.ClosedUtc == message.CreatedUtc)
            .Select(e => (Exchange: e, Asked: export.Messages.FirstOrDefault(m => m.ExchangeId == e.ExchangeId)))
            .Where(q => q.Asked is null || q.Asked.From == message.From)
            .OrderBy(q => q.Exchange.ExchangeId)
            .Select(q => q.Asked is { } asked && PayloadShape.Spoken(asked.PayloadJson) is { Length: > 0 } said
                ? Short(said)
                : $"exchange {q.Exchange.ExchangeId}")];
    }

    /// <summary>The start of a question, short enough to list several on one line.</summary>
    /// <param name="said">The words.</param>
    /// <returns>The first sixty characters, on one line, with an ellipsis when cut.</returns>
    private static string Short(string said)
    {
        var flat = string.Join(' ', said.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (flat.Length <= 60)
        {
            return flat;
        }

        var at = char.IsHighSurrogate(flat[58]) ? 58 : 59;

        return string.Concat(flat.AsSpan(0, at), "\u2026");
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
                ExchangeOutcome.Settled => $"settled by the agent that asked, in exchange {snapshot.SettledBy}",
                _ => "closed"
            };
    }

    /// <summary>Who said this to whom, and when.</summary>
    /// <param name="message">The message.</param>
    /// <param name="zone">The zone to render the time in.</param>
    /// <returns>The header line.</returns>
    /// <remarks>
    /// <b>The recipient is in the header, unlike the type and the state.</b> A conversation is a set of pairwise
    /// exchanges (SPEC.md §5.0), so one line said to three agents is three messages — and which of them a
    /// message is addressed to is the one thing a reader cannot work out from the words.
    /// </remarks>
    private static string Header(Message message, TimeZoneInfo? zone) =>
        $"{LocalTime.Write(message.CreatedUtc, zone)}  {Speaker.From(message)} -> {Speaker.To(message)}";

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
    }

    /// <summary>One outcome as a line of text.</summary>
    /// <param name="point">The outcome.</param>
    /// <returns>Its id, how it came out, and its words.</returns>
    public static string Point(PayloadPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        var tag = point.Outcome switch
        {
            null => null,
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

        // Every break .NET itself breaks at (CR, LF, CRLF, NEL, form feed, U+2028, U+2029), never only CR and LF: a surface
        // drawing these rows breaks them that way again, and text after a break this missed began a row with no indent,
        // where only a header may stand (review, lens 2 run 81).
        return text.ReplaceLineEndings("\n").Split('\n');
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
