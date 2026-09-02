using Parley.Client.Rendering;
using Parley.Client.Seating;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// What is waiting for a seated person, and answering what they owe.
/// </summary>
/// <remarks>
/// <para>
/// Debts first, per SPEC.md §7.3. A question somebody owes an answer to is the one thing on this
/// screen that is costing another agent its turn; an answer or a notice has already done its
/// work by being read.
/// </para>
/// <para>
/// <b>Draining is a state change and the surface says so before doing it.</b> A drain marks
/// messages delivered and starts the working window of §6 on anything owed — so it is not a look
/// at an inbox, it is taking delivery of one, and a person who discovered that afterwards would
/// have started a clock they did not know was running.
/// </para>
/// </remarks>
internal static class Waiting
{
    /// <summary>Shows what is waiting, and offers to take or answer it.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="voice">The person's own agent operations.</param>
    /// <param name="hold">The seat they are holding.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>When the person goes back.</returns>
    internal static async Task ShowAsync(
        IAnsiConsole console,
        SeatVoice voice,
        SeatHold hold,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (messages, polls) = await voice.WaitingAsync(cancellationToken)
                .ConfigureAwait(false);

            console.WriteLine();
            console.MarkupLine(
                $"[bold]{messages}[/] waiting, [bold]{polls}[/] closure poll(s). "
                + $"[dim]{hold.Delivered.Count} taken so far.[/]");

            Held(console, hold);

            var owed = hold.Delivered.Where(one => one.Owes).ToList();

            var choices = new List<string> { Take };

            if (owed.Count > 0)
            {
                choices.Add(Answer);
            }

            if (hold.Delivered.Any(one => !one.Owes))
            {
                choices.Add(Clear);
            }

            choices.Add(Back);

            switch (console.Prompt(
                new SelectionPrompt<string>().Title("Inbox").AddChoices(choices)))
            {
                case Take:
                    await TakeAsync(console, voice, hold, cancellationToken).ConfigureAwait(false);
                    break;

                case Answer:
                    await Answering.RunAsync(console, voice, hold, owed, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Clear:
                    var dropped = hold.DismissRead();
                    console.MarkupLine(
                        $"[dim]{dropped} cleared. Anything you owe an answer to is still here.[/]");
                    break;

                default:
                    return;
            }
        }
    }

    /// <summary>Takes delivery, having said what that means.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="voice">The person's own agent operations.</param>
    /// <param name="hold">The seat to record against.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>When the drain has finished.</returns>
    /// <remarks>
    /// Confirmed rather than done on a keystroke, because the sentence above the prompt is the
    /// only warning a person gets that a clock starts here.
    /// </remarks>
    private static async Task TakeAsync(
        IAnsiConsole console,
        SeatVoice voice,
        SeatHold hold,
        CancellationToken cancellationToken)
    {
        console.MarkupLine(
            "[yellow]Taking delivery marks these messages delivered[/] and starts the working "
            + "window on anything you then owe. It is not a look at the inbox.");

        if (!console.Confirm("Take delivery?"))
        {
            return;
        }

        // One call, and the recording is inside it. Draining and then recording separately is
        // two surfaces' worth of the same rule, which is how they come to disagree about what a
        // person has taken -- so the drive lives in Parley.Client beside the state it changes.
        var (taken, polls) = await voice.TakeDeliveryAsync(hold, cancellationToken)
            .ConfigureAwait(false);

        console.MarkupLine(
            taken.Count == 0 && polls == 0
                ? "[dim]Nothing was waiting.[/]"
                : $"[green]{taken.Count} taken.[/] [dim]The working window is running on "
                  + "anything you now owe.[/]");
    }

    /// <summary>Draws what the person is already holding.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="hold">The seat.</param>
    /// <remarks>
    /// The owed column is a word before it is a position. Ordering debts first helps an eye, and
    /// would carry the whole distinction if the word were not there — which is the colour-alone
    /// failure in a different costume (FR-005).
    /// </remarks>
    private static void Held(IAnsiConsole console, SeatHold hold)
    {
        if (hold.Delivered.Count == 0)
        {
            console.MarkupLine("[dim]You are holding nothing. Take delivery to see what is waiting.[/]");

            return;
        }

        var table = new Table().Border(TableBorder.Rounded);

        table.AddColumn("from");
        table.AddColumn("kind");
        table.AddColumn("owed");
        table.AddColumn("says");

        foreach (var one in hold.Delivered.OrderByDescending(d => d.Owes))
        {
            var payload = Payload.Read(one.Message.PayloadJson);

            table.AddRow(
                Safe.Text(one.Message.From.Value),
                Safe.Text(one.Message.Type.ToString().ToLowerInvariant()),
                one.Owes ? "[yellow]you owe an answer[/]" : "[dim]nothing owed[/]",
                Safe.Text(Shorten(payload.Raw is { Length: > 0 } raw ? raw : payload.Said)));
        }

        console.Write(table);
    }

    /// <summary>Keeps one row to one row.</summary>
    /// <param name="text">What the message said.</param>
    /// <returns>The first part of it.</returns>
    private static string Shorten(string text)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();

        return flattened.Length <= 80 ? flattened : flattened[..77] + "...";
    }

    private const string Take = "Take delivery";
    private const string Answer = "Answer something you owe";
    private const string Clear = "Clear what is read and owes nothing";
    private const string Back = "Back";
}
