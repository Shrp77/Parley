using Parley.Client.Rendering;
using Parley.Client.Seating;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Answering a question the person owes, one sub-question at a time.
/// </summary>
/// <remarks>
/// <para>
/// The sub-question identifiers come from the broker rather than from the delivered payload.
/// SPEC.md §5.3 answers by identifier and the store holds what was actually asked; a form built
/// from a document this process parsed would be a second opinion about it, and the one that
/// disagrees is the one that produces an answer resolving a sub-question nobody asked.
/// </para>
/// <para>
/// <c>ghosted</c> is not offered, because §5.3 reserves it for the broker recording an exchange
/// nobody returned to. An agent — or a person — able to write it over their own unanswered work
/// would be manufacturing the evidence §15 counts to find an unhealthy deployment. The list comes
/// from <see cref="SeatVoice.Outcomes"/>, so this surface cannot quietly grow a fourth.
/// </para>
/// </remarks>
internal static class Answering
{
    /// <summary>Answers one of the questions the person owes.</summary>
    /// <param name="console">Where to draw, and where the answer is typed.</param>
    /// <param name="voice">The person's own agent operations.</param>
    /// <param name="hold">The seat holding the question.</param>
    /// <param name="owed">The questions that are owed.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>When the answer has been sent, or abandoned.</returns>
    internal static async Task RunAsync(
        IAnsiConsole console,
        SeatVoice voice,
        SeatHold hold,
        IReadOnlyList<Delivered> owed,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, Delivered>(StringComparer.Ordinal);

        foreach (var one in owed)
        {
            labels[Label(one)] = one;
        }

        var picked = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Which question?")
                .PageSize(15)
                .AddChoices([.. labels.Keys, Back]));

        if (picked == Back || !labels.TryGetValue(picked, out var chosen))
        {
            return;
        }

        if (chosen.Message.ExchangeId is not { } exchange)
        {
            console.MarkupLine("[yellow]That message is not part of an exchange to answer.[/]");

            return;
        }

        Show(console, chosen);

        var ids = await voice.SubQuestionsAsync(exchange, cancellationToken).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            console.MarkupLine("[yellow]The broker says that exchange asked nothing to resolve.[/]");

            return;
        }

        var resolutions = new List<(string Id, string Text, string Outcome)>();

        foreach (var id in ids)
        {
            console.WriteLine();
            console.MarkupLine($"[bold]{Safe.Text(id)}[/]");

            var outcome = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("How does this resolve?")
                    .AddChoices(SeatVoice.Outcomes.Select(o => o.Says)));

            var value = SeatVoice.Outcomes.First(o => o.Says == outcome).Value;

            var text = console.Prompt(
                new TextPrompt<string>("Your answer:")
                    .Validate(said => string.IsNullOrWhiteSpace(said)
                        ? ValidationResult.Error(
                            "[red]An empty resolution closes the exchange while telling the asker "
                            + "nothing, and they cannot ask again.[/]")
                        : ValidationResult.Success()));

            resolutions.Add((id, text, value));
        }

        // Through the overload that settles the seat as well, so this surface never strikes a
        // question off its own list while the broker still believes it open.
        var answered = await voice
            .AnswerAsync(hold, exchange, resolutions, cancellationToken)
            .ConfigureAwait(false);

        console.MarkupLine(
            answered.Closed
                ? $"[green]{Safe.Text(answered.Says)}[/]"
                : $"[yellow]{Safe.Text(answered.Says)}[/]");
    }

    /// <summary>Shows the question as it was asked.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="owed">The delivered question.</param>
    /// <remarks>
    /// Intent and consequences included, because rule 8.1 requires an asker to state them and an
    /// answer written without reading them is an answer to a narrower question than was asked.
    /// </remarks>
    private static void Show(IAnsiConsole console, Delivered owed)
    {
        var payload = Payload.Read(owed.Message.PayloadJson);
        var body = new System.Text.StringBuilder();

        body.Append(Safe.Text(
            payload.Raw is { Length: > 0 } raw ? raw : payload.Said));

        foreach (var point in payload.Points)
        {
            body.Append("\n  [dim]·[/] ")
                .Append(Safe.Text(point.Id))
                .Append(": ")
                .Append(Safe.Text(point.Text));
        }

        if (payload.Consequences is { Length: > 0 } consequences)
        {
            body.Append("\n\n[dim]Consequences: ")
                .Append(Safe.Text(consequences))
                .Append("[/]");
        }

        console.Write(new Panel(body.ToString())
        {
            Header = new PanelHeader($" from {Safe.Text(owed.Message.From.Value)} "),
            Border = BoxBorder.Rounded,
            Expand = true,
        });
    }

    /// <summary>How one owed question reads in the chooser.</summary>
    /// <param name="owed">The delivered question.</param>
    /// <returns>Who asked, and the start of what they asked.</returns>
    private static string Label(Delivered owed)
    {
        var payload = Payload.Read(owed.Message.PayloadJson);
        var said = (payload.Raw is { Length: > 0 } raw ? raw : payload.Said)
            .ReplaceLineEndings(" ")
            .Trim();

        return Safe.Text(
            $"{owed.Message.From.Value}: {(said.Length <= 60 ? said : said[..57] + "...")}");
    }

    private const string Back = "Back";
}
