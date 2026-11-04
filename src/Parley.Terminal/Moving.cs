using System.Globalization;
using System.Text;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>Moving whole exchanges to another conversation (SPEC.md §5.0, <c>move_exchanges</c>).</summary>
/// <remarks>
/// <para>
/// <c>MoveExchangesAsync</c> and <c>PreviewMoveAsync</c> live here and nowhere else in this surface, carrying their door: the
/// person ticks what to move, reads a list of everything that would go, and then names or chooses where. The first row of
/// that choice leaves them where they are.
/// </para>
/// <para>
/// <b>Nothing wakes anybody.</b> An agent party to an open exchange that moved is told at its next collection.
/// </para>
/// </remarks>
internal static class Moving
{
    /// <summary>The action's name, on the status line and in the menu, and the dialog that picks.</summary>
    internal const string Title = "Move entries to another conversation";

    /// <summary>The dialog that lists everything that would go.</summary>
    internal const string ReviewTitle = "These entries will move";

    /// <summary>The dialog that asks where.</summary>
    internal const string WhereTitle = "Where do these entries go?";

    /// <summary>The dialog that names a new conversation.</summary>
    internal const string NameTitle = "Name the new conversation";

    /// <summary>The first row of the last choice: the safe one.</summary>
    internal const string Stay = "Leave them where they are";

    /// <summary>The row that asks for a new conversation's name.</summary>
    internal const string New = "A new conversation";

    private const int FilesNamed = 12;

    private const string Picking = "A message can't leave its exchange, so a whole exchange goes. Tick what to move.";

    /// <summary>Opens the dialogs that move some exchanges.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="source">The conversation they are in.</param>
    /// <param name="under">The exchange under the cursor, which starts ticked, or <see langword="null"/>.</param>
    /// <param name="queue">Runs the reads and the move off the loop.</param>
    /// <param name="done">Says what happened, and is told where they went when they moved.</param>
    /// <param name="token">Cancels the first read.</param>
    /// <returns>When the first dialog has been opened.</returns>
    internal static async Task OpenAsync(
        IApplication app,
        BrokerClient client,
        string source,
        long? under,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, string?, Task> done,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var export = await client.ExportAsync(source, token).ConfigureAwait(false);

        if (export.Exchanges.Count == 0)
        {
            await done("There is nothing in this conversation to move.", null).ConfigureAwait(false);

            return;
        }

        var everything = await client
            .PreviewMoveAsync(source, [.. export.Exchanges.Select(e => e.ExchangeId)], token).ConfigureAwait(false);

        if (everything.Refused is { } refused)
        {
            await done(refused, null).ConfigureAwait(false);

            return;
        }

        var rows = everything.Exchanges
            .Select(e => (e.Id, Row: new Tickable(Label(e, []), string.Empty, e.Id == under)))
            .ToList();

        var ticking = new TickDialog(Title, Picking, [.. rows.Select(r => r.Row)], showsWholeRow: true);
        var opened = Prompting.Open(app, ticking);

        ticking.Done += (_, chosen) =>
        {
            Prompting.Close(app, opened);

            var ticked = chosen is null
                ? []
                : rows.Where(r => chosen.Any(c => c.Ticked && c.Label == r.Row.Label)).Select(r => r.Id).ToList();

            if (ticked.Count == 0)
            {
                _ = done(chosen is null ? string.Empty : "Nothing was picked, so nothing moved.", null);

                return;
            }

            queue(reading => ReviewAsync(app, client, source, export, ticked, queue, done, reading));
        };
    }

    /// <summary>Lists everything that would go, every row of it, in a list that scrolls.</summary>
    private static async Task ReviewAsync(
        IApplication app,
        BrokerClient client,
        string source,
        ConversationExport export,
        IReadOnlyList<long> ticked,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, string?, Task> done,
        CancellationToken token)
    {
        var whole = MovePicking.WithSendsToSeveral(export.Messages, ticked);
        var preview = await client.PreviewMoveAsync(source, whole, token).ConfigureAwait(false);

        if (preview.Refused is { } refused)
        {
            await done(refused, null).ConfigureAwait(false);

            return;
        }

        var destinations = await MovePicking.DestinationsAsync(client, source, token).ConfigureAwait(false);

        var rows = preview.Exchanges
            .Select(e => new Tickable(Label(e, ticked), string.Empty, Ticked: true))
            .ToList();

        var reviewing = new TickDialog(ReviewTitle, Describe(preview, source, ticked), rows, showsWholeRow: true);
        var opened = Prompting.Open(app, reviewing);

        reviewing.Done += (_, chosen) =>
        {
            Prompting.Close(app, opened);

            if (chosen is null)
            {
                _ = done("Nothing moved.", null);

                return;
            }

            IReadOnlyList<long> left =
            [
                .. preview.Exchanges
                    .Where(e => chosen.Any(c => c.Ticked && c.Label == Label(e, ticked)))
                    .Select(e => e.Id),
            ];

            if (left.Count == 0)
            {
                _ = done("Nothing was left ticked, so nothing moved.", null);

                return;
            }

            // A send to several or a Request to a role stays whole whatever was unticked. The broker keeps no record of the
            // first, so nothing else can hold it, and the next step says how many will go.
            var kept = MovePicking.Whole(export, left);

            AskWhere(app, client, source, kept, destinations, queue, done);
        };
    }

    /// <summary>Asks where they go. The first row leaves them where they are.</summary>
    private static void AskWhere(
        IApplication app,
        BrokerClient client,
        string source,
        IReadOnlyList<long> exchanges,
        IReadOnlyList<ConversationSummary> destinations,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, string?, Task> done)
    {
        var rows = destinations
            .Select(c => (c.Id, Row: Safe.Text(ConversationWording.Listed(c.Id, c.Parent))))
            .ToList();

        var choosing = Prompting.Choose(
            WhereTitle,
            string.Create(CultureInfo.InvariantCulture, $"{exchanges.Count} exchange{(exchanges.Count == 1 ? string.Empty : "s")} will move."),
            [Stay, New, .. rows.Select(r => r.Row)]);

        var opened = Prompting.Open(app, choosing);

        choosing.Done += (_, picked) =>
        {
            Prompting.Close(app, opened);

            if (picked is null or Stay)
            {
                _ = done("Nothing moved.", null);

                return;
            }

            if (!string.Equals(picked, New, StringComparison.Ordinal))
            {
                var existing = rows.First(r => r.Row == picked).Id;

                queue(moving => MoveAsync(client, source, existing, false, exchanges, destinations, app, queue, done, moving));

                return;
            }

            var naming = Prompting.Text(
                NameTitle,
                "What is the new conversation called?\n\n" + ConversationName.Rule,
                problem: said => string.IsNullOrWhiteSpace(said) || ConversationName.Correct(said.Trim()) != said.Trim()
                    ? ConversationName.Rule
                    : null,
                correct: ConversationName.Correct,
                corrected: ConversationName.WasCorrected);

            var asked = Prompting.Open(app, naming);

            naming.Done += (_, name) =>
            {
                Prompting.Close(app, asked);

                if (string.IsNullOrWhiteSpace(name))
                {
                    _ = done("Nothing moved.", null);

                    return;
                }

                queue(moving => MoveAsync(client, source, name.Trim(), true, exchanges, destinations, app, queue, done, moving));
            };
        };
    }

    /// <summary>Moves them and says what happened.</summary>
    private static async Task MoveAsync(
        BrokerClient client,
        string source,
        string destination,
        bool isNew,
        IReadOnlyList<long> exchanges,
        IReadOnlyList<ConversationSummary> destinations,
        IApplication app,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, string?, Task> done,
        CancellationToken token)
    {
        var moved = await client.MoveExchangesAsync(source, destination, isNew, exchanges, token).ConfigureAwait(false);

        // Asked again, so what was picked is not lost to a name that was taken.
        if (moved.Refused is { } refused)
        {
            await done($"Nothing moved: {refused}", null).ConfigureAwait(false);

            AskWhere(app, client, source, exchanges, destinations, queue, done);

            return;
        }

        await done(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Moved {moved.Exchanges} exchange{(moved.Exchanges == 1 ? string.Empty : "s")} ({moved.Messages} message{(moved.Messages == 1 ? string.Empty : "s")}) to {destination}."),
            destination).ConfigureAwait(false);
    }

    /// <summary>Why an exchange goes though it was not picked, or null when it was picked.</summary>
    private static string? AddedBecause(MovePreviewExchange exchange, IReadOnlyCollection<long> ticked) =>
        exchange.AddedBecause ?? (ticked.Count == 0 || ticked.Contains(exchange.Id) ? null : MovePicking.SentTogether);

    /// <summary>The door: what happens to what is listed below it.</summary>
    private static string Describe(MovePreview preview, string source, IReadOnlyCollection<long> ticked)
    {
        var messages = preview.Exchanges.Sum(e => e.Messages);
        var said = new StringBuilder(string.Create(
            CultureInfo.InvariantCulture,
            $"{preview.Exchanges.Count} exchange{(preview.Exchanges.Count == 1 ? string.Empty : "s")} and {messages} message{(messages == 1 ? string.Empty : "s")}. "));

        said.Append(Safe.Text($"Each keeps its words, author and time, and you can move them back unless {source} has closed. "));

        if (preview.Exchanges.Any(e => e.Open))
        {
            said.Append("Open work carries on in the new place; agents aren't woken, and are told when they next collect. ");
        }

        foreach (var because in preview.Exchanges.Select(e => AddedBecause(e, ticked)).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            said.Append(Safe.Text($"A row marked added goes though you didn't tick it: {because} "));
        }

        said.Append("Anything asked as a result stays behind unless you ticked it. ");

        // Cut, or a conversation with many files would push the list itself off the screen. An agent chooses these names.
        if (preview.FilesStaying.Count > 0)
        {
            var named = string.Join(", ", preview.FilesStaying.Take(FilesNamed));
            var more = preview.FilesStaying.Count > FilesNamed
                ? string.Create(CultureInfo.InvariantCulture, $" and {preview.FilesStaying.Count - FilesNamed} more")
                : string.Empty;

            said.Append(Safe.Text($"These files stay in {source}: {named}{more}. "));
        }

        return said.Append("Untick anything you meant to leave; a message to several, or to a role, goes whole.").ToString();
    }

    /// <remarks>
    /// What a person decides on comes first, then the words that tell two rows apart, and who and how many last. A row can be
    /// wider than the dialog and no key scrolls sideways, so the dialog also draws the row under the cursor whole.
    /// </remarks>
    private static string Label(MovePreviewExchange exchange, IReadOnlyCollection<long> ticked)
    {
        IEnumerable<string> marks =
        [
            .. exchange.Open ? ["still open"] : Array.Empty<string>(),
            .. AddedBecause(exchange, ticked) is null ? Array.Empty<string>() : ["added"],
        ];
        var marked = string.Join(", ", marks);

        return Safe.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"{exchange.Id}: {(marked.Length == 0 ? string.Empty : marked + ": ")}{exchange.FirstWords} ({exchange.Requester.Value} to {exchange.Receiver.Value}, {exchange.Messages} message{(exchange.Messages == 1 ? string.Empty : "s")})"));
    }
}
