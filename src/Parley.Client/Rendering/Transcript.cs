using System.Globalization;
using System.Text;
using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>
/// A conversation written out as text, with the visibility annotations intact.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than inside the page handler so it can be tested against the case it exists for.
/// An export that dropped the annotations would be the misleading artefact the whole view exists
/// to prevent, one step further from anybody who could catch it — read somewhere else, later, by
/// somebody who never saw the page and has no way to know what was left out.
/// </para>
/// <para>
/// It writes what <see cref="Visibility"/> derives and nothing more. The page and the file
/// therefore cannot disagree about who saw what, which is the only property worth having here:
/// two renderings of one conversation that annotate it differently are worse than one that does
/// not annotate it at all.
/// </para>
/// </remarks>
public static class Transcript
{
    /// <summary>Writes a conversation as readable text.</summary>
    /// <param name="conversationId">The conversation's identifier.</param>
    /// <param name="project">The project it was read from.</param>
    /// <param name="messages">Its messages, in the order the broker recorded them.</param>
    /// <param name="exchanges">The state of every exchange those messages belong to.</param>
    /// <returns>The transcript.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static string Write(
        string? conversationId,
        string? project,
        IReadOnlyList<Message> messages,
        IReadOnlyList<ExchangeSnapshot> exchanges)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return Write(conversationId, project, new ConversationExport(messages, exchanges ?? []));
    }

    /// <summary>Writes everything an export returned as readable text: the messages, and what else it says.</summary>
    /// <param name="conversationId">The conversation's identifier.</param>
    /// <param name="project">The project it was read from.</param>
    /// <param name="export">What the broker exported.</param>
    /// <returns>The transcript.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="export"/> is null.</exception>
    /// <remarks>
    /// Everything SPEC.md §10.2 says export returns, as the JSON file carries it. A destroyed conversation has no
    /// messages, which is also what a store that lost something would give; without the destroyed record the file
    /// read as a conversation that never had any. Times stay in UTC, as everywhere else in this file.
    /// </remarks>
    public static string Write(string? conversationId, string? project, ConversationExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var messages = export.Messages;
        var exchanges = export.Exchanges;
        var heading = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Conversation {conversationId} in project {project}")
            .AppendLine();

        // The item's title is the person's text; flattened like a payload, so it cannot put a line of its own here.
        if (export.Item is { } item)
        {
            heading.Append("Roadmap: ").AppendLine(OneLine(Parley.Client.Roadmaps.ItemWording.Belongs(item)));
        }

        if (export.Destroyed is { } gone)
        {
            var between = gone.Participants.Count == 0 ? "nobody" : string.Join(", ", gone.Participants.Select(p => p.Value));

            return heading
                .Append(
                    CultureInfo.InvariantCulture,
                    $"Destroyed on {gone.DestroyedUtc.ToString("u", CultureInfo.InvariantCulture)}. ")
                .Append(
                    CultureInfo.InvariantCulture,
                    $"It held {gone.Messages} message{(gone.Messages == 1 ? string.Empty : "s")}, between {between}.")
                .AppendLine()
                .AppendLine("Nothing of what was said was kept, and it cannot be undone.")
                .ToString();
        }

        var participants = Visibility.Participants(messages);

        // Assigned rather than ToDictionary'd. A broker that returned the same exchange twice is
        // a broker defect, and the right outcome is an export that says what it knows rather than
        // no export at all: this is the copy somebody takes away when something has gone wrong,
        // which is exactly when a duplicate is most likely.
        var states = new Dictionary<long, ExchangeSnapshot>();

        foreach (var exchange in exchanges ?? [])
        {
            states[exchange.ExchangeId] = exchange;
        }

        // What each reply settled (SPEC.md §5.6) is read from the same record the screens read it from.
        var whole = new ConversationExport([.. messages], [.. states.Values], null);

        var written = heading
            .Append(
                CultureInfo.InvariantCulture,
                $"Participants: {string.Join(", ", participants.Select(p => p.Value))}")
            .AppendLine()
            .AppendLine("Pairwise throughout: a message was seen by its sender and its recipient,")
            .AppendLine("and by nobody else. Where a participant was not party to one, it says so.")
            .AppendLine();

        foreach (var message in messages)
        {
            written
                .Append(
                    CultureInfo.InvariantCulture,
                    $"[{message.CreatedUtc.ToString("u", CultureInfo.InvariantCulture)}] ")
                .Append(CultureInfo.InvariantCulture, $"{Speaker.From(message)} -> {Speaker.To(message)} ")
                .Append(
                    CultureInfo.InvariantCulture,
                    $"({message.Type}, answered by {message.ResponderModel}, "
                    + $"{Delivery.Of(message, TimeZoneInfo.Utc)})")
                .AppendLine();

            if (states.TryGetValue(message.ExchangeId, out var snapshot))
            {
                written
                    .Append(CultureInfo.InvariantCulture, $"  exchange {message.ExchangeId}: {Reading.State(snapshot)}")
                    .AppendLine();
            }

            written.Append("  ").AppendLine(OneLine(message.PayloadJson));

            if (Reading.Settled(message, whole) is { Count: > 0 } settled)
            {
                // Each question's start is the asker's own words, so it takes the payload's rule: Reading.Settled
                // collapses whitespace and nothing else, and an escape sequence in a question reached the file raw.
                written.Append("  ").AppendLine(OneLine(Reading.SettledLine(settled)));
            }

            if (Reading.RoleRequest(message, whole) is { } role)
            {
                written.Append("  ").AppendLine(role);
            }

            var absent = Visibility.Describe(message, participants);

            if (absent.Length > 0)
            {
                written.Append("  ").AppendLine(absent);
            }

            written.AppendLine();
        }

        return written.ToString();
    }

    /// <summary>Flattens a payload so it cannot occupy a line of its own, or do anything but read as text.</summary>
    /// <param name="payload">The message's payload, as the agent wrote it.</param>
    /// <returns>The payload on one line, with nothing a terminal or editor would act on.</returns>
    /// <remarks>
    /// <para>
    /// The one place this file is not a straight copy of what the broker returned, and the reason
    /// is a forgery. Message text is written by a peer; a payload carrying a newline followed by
    /// a line shaped like an annotation would sit in the export at the same indentation as the
    /// real ones — and the annotation is precisely the line a reader trusts, because it is the
    /// one the console is supposed to be speaking.
    /// </para>
    /// <para>
    /// The page has no such problem: Razor encodes, so a payload is content wherever it lands. A
    /// text file has no default of that kind, so the structure has to carry it — one line per
    /// message, and a line that begins with two spaces is the console's, never a peer's.
    /// </para>
    /// <para>
    /// Escaped rather than stripped. A reader who wants the exact bytes has the raw export;
    /// silently deleting characters from an artefact somebody may quote would be its own small
    /// dishonesty.
    /// </para>
    /// <para>
    /// The line breaks and nothing else. Escaping backslashes too would be the more thorough
    /// habit and the wrong one here: <c>json_valid</c> is enforced on the column (§13), and JSON
    /// allows a raw line break only as whitespace between tokens, never inside a string — so the
    /// breaks escaped here are the only raw ones a payload can hold, and every word of it sits in a
    /// string, where <c>\n</c> already appears as those two characters. Doubling them would make
    /// the common case render wrongly to defend a case the schema does not permit.
    /// </para>
    /// <para>
    /// Every other character <see cref="PrintableLine.Breaks"/> names is then replaced one for one, as the command
    /// line's own neutraliser does. JSON refuses a raw C0 control inside a string but not DEL, a C1 control such as
    /// <c>U+009B</c> (a terminal's one-byte CSI), or <c>U+202E</c>, and the downloaded file is read with <c>cat</c> or
    /// <c>type</c>, which hand an escape sequence to the terminal to clear the screen or set the clipboard, or in an
    /// editor that reverses the line after an override (lens 2 run 139).
    /// </para>
    /// </remarks>
    private static string OneLine(string? payload)
    {
        var flat = (payload ?? string.Empty)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        var inert = new StringBuilder(flat.Length);

        foreach (var rune in flat.EnumerateRunes())
        {
            inert.Append(PrintableLine.Breaks(rune) ? "?" : rune.ToString());
        }

        return inert.ToString();
    }
}
