using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// One conversation, read: who asked, who answered, and who could see it.
/// </summary>
/// <remarks>
/// <para>
/// The ordering, the participants and the per-message visibility all come from
/// <c>Parley.Client.Rendering</c> — the same code the console renders its transcript from.
/// Deriving them again here is how two surfaces come to disagree about who took part in a
/// conversation, which SC-003 forbids and which this repository has already shipped twice: a
/// frame disagreeing with the page beneath it about a waiting count, and again about a seat.
/// </para>
/// <para>
/// <b>Visibility is text.</b> <see cref="Visibility.Describe"/> names who did not see a message,
/// in words, and the colour beside an author only helps an eye follow one agent down a long
/// thread. Nothing here says anything the words do not (FR-005, SC-007), which matters more in a
/// terminal than in a browser: a terminal may have no colour at all.
/// </para>
/// </remarks>
internal static class TranscriptView
{
    /// <summary>Reads one conversation and draws it.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="project">The project it is in.</param>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>When the person has finished reading.</returns>
    internal static async Task ShowAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        ProjectSettings project,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ExportAsync(conversationId, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!read.Reachable)
        {
            console.MarkupLine($"[red]{ProjectList.Escape(read.Problem!)}[/]");

            return;
        }

        var export = read.Value;

        if (export is null || export.Messages.Count == 0)
        {
            console.MarkupLine("[yellow]That conversation has no messages.[/]");

            return;
        }

        Draw(console, conversationId, project, export);
    }

    /// <summary>Writes the messages out, one panel each.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="project">The project it came from.</param>
    /// <param name="export">The messages and the state of their exchanges.</param>
    private static void Draw(
        IAnsiConsole console,
        string conversationId,
        ProjectSettings project,
        ConversationExport export)
    {
        var participants = Visibility.Participants(export.Messages);

        // Assigned rather than built by ToDictionary. A broker that returned one exchange twice is
        // a broker defect, and the right outcome is a transcript that says what it knows rather
        // than none at all -- this is what somebody reads when something has already gone wrong,
        // which is exactly when a duplicate is most likely.
        var states = new Dictionary<long, ExchangeSnapshot>();

        foreach (var exchange in export.Exchanges ?? [])
        {
            states[exchange.ExchangeId] = exchange;
        }

        console.WriteLine();
        console.MarkupLine(
            $"[bold]{ProjectList.Escape(conversationId)}[/] "
            + $"[dim]in {ProjectList.Escape(project.Name.Value)} — "
            + $"{participants.Count} participants, {export.Messages.Count} messages[/]");
        console.MarkupLine(
            "[dim]Pairwise throughout: a message was seen by its sender and its recipient, and by "
            + "nobody else. Where a participant was not party to one, it says so.[/]");

        foreach (var message in export.Messages)
        {
            console.Write(Panel(message, participants, states));
        }

        console.WriteLine();
    }

    /// <summary>One message, with its attribution and who missed it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="participants">Everybody in the conversation.</param>
    /// <param name="states">Where each exchange stands, by exchange identifier.</param>
    /// <returns>The panel.</returns>
    /// <remarks>
    /// The body goes in as escaped text. A message is agent-written and may contain anything,
    /// square brackets included, and a terminal that read those as style tags would let a sender
    /// choose how their words are drawn — the same injection the console refuses by never
    /// emitting raw output.
    /// </remarks>
    internal static Panel Panel(
        Message message,
        IReadOnlyList<AgentName> participants,
        IReadOnlyDictionary<long, ExchangeSnapshot> states)
    {
        var payload = Payload.Read(message.PayloadJson);
        var body = new System.Text.StringBuilder();

        // Whole and as stored when it is not a shape the reader knows. Guessing at a structure the
        // document does not have would be the one way to make a transcript say something the agent
        // did not -- which is the whole thing a transcript is for.
        body.Append(ProjectList.Escape(
            payload.Raw is { Length: > 0 } raw ? raw : payload.Said));

        foreach (var point in payload.Points)
        {
            body.Append("\n  [dim]·[/] ")
                .Append(ProjectList.Escape(point.Id))
                .Append(": ")
                .Append(ProjectList.Escape(point.Text));

            if (point.Resolution is { Length: > 0 } resolution)
            {
                body.Append(" [dim](").Append(ProjectList.Escape(resolution)).Append(")[/]");
            }
        }

        if (payload.Consequences is { Length: > 0 } consequences)
        {
            body.Append("\n\n[dim]Consequences: ")
                .Append(ProjectList.Escape(consequences))
                .Append("[/]");
        }

        // In words, never by colour. Visibility.Describe names who was not party to this message,
        // and a terminal may have no colour at all -- so anything carried by it alone would be
        // carried by nothing (FR-005, SC-007).
        var unseen = Visibility.Describe(message, participants);

        if (unseen.Length > 0)
        {
            body.Append("\n\n[yellow]").Append(ProjectList.Escape(unseen)).Append("[/]");
        }

        var exchange = message.ExchangeId is { } id && states.TryGetValue(id, out var snapshot)
            ? $"[dim] · exchange {id} {ProjectList.Escape(snapshot.State.ToString().ToLowerInvariant())}[/]"
            : string.Empty;

        return new Panel(body.ToString())
        {
            Header = new PanelHeader(
                $" {ProjectList.Escape(message.From.Value)} "
                + $"[dim]to[/] {ProjectList.Escape(message.To.Value)} "
                + $"[dim]· {ProjectList.Escape(message.Type.ToString().ToLowerInvariant())}[/]"
                + exchange + " "),
            Border = BoxBorder.Rounded,
            Expand = true,
        };
    }
}
