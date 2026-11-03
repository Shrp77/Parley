using System.Globalization;
using System.Text;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>Moving whole exchanges to another conversation (SPEC.md §5.0, <c>move_exchanges</c>).</summary>
/// <remarks>
/// <para>
/// <c>MoveExchangesAsync</c> and <c>PreviewMoveAsync</c> live here and nowhere else in this surface, carrying their door: the
/// person ticks what to move, reads everything that would go, and then names or chooses where in that same step. The first
/// row of that choice leaves them where they are.
/// </para>
/// <para>
/// <b>Nothing wakes anybody.</b> An agent party to an open exchange that moved is told at its next collection.
/// </para>
/// </remarks>
internal static class Moving
{
    /// <summary>The action's name, on the status line and in the menu.</summary>
    internal const string Title = "Move entries to another conversation";

    /// <summary>The first row of the last choice: the safe one.</summary>
    internal const string Stay = "Leave them where they are";

    /// <summary>The row that asks for a new conversation's name.</summary>
    internal const string New = "A new conversation";

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
        Func<string, string?, IReadOnlyList<long>, Task> done,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var export = await client.ExportAsync(source, token).ConfigureAwait(false);
        var everything = await client
            .PreviewMoveAsync(source, [.. export.Exchanges.Select(e => e.ExchangeId)], token).ConfigureAwait(false);

        if (everything.Refused is { } refused)
        {
            await done(refused, null, []).ConfigureAwait(false);

            return;
        }

        var rows = everything.Exchanges
            .Select(e => (e.Id, Row: new Tickable(Label(e), e.Open ? "still open" : string.Empty, e.Id == under)))
            .ToList();

        var ticking = new TickDialog(Title, Picking, [.. rows.Select(r => r.Row)]);
        var opened = Prompting.Open(app, ticking);

        ticking.Done += (_, chosen) =>
        {
            Prompting.Close(app, opened);

            var ticked = chosen is null
                ? []
                : rows.Where(r => chosen.Any(c => c.Ticked && c.Label == r.Row.Label)).Select(r => r.Id).ToList();

            if (ticked.Count == 0)
            {
                _ = done(chosen is null ? string.Empty : "Nothing was picked, so nothing moved.", null, []);

                return;
            }

            queue(reading => ConfirmAsync(app, client, source, ticked, queue, done, reading));
        };
    }

    /// <summary>Says everything that would go, and asks where.</summary>
    private static async Task ConfirmAsync(
        IApplication app,
        BrokerClient client,
        string source,
        IReadOnlyList<long> ticked,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, string?, IReadOnlyList<long>, Task> done,
        CancellationToken token)
    {
        var preview = await client.PreviewMoveAsync(source, ticked, token).ConfigureAwait(false);

        if (preview.Refused is { } refused)
        {
            await done(refused, null, []).ConfigureAwait(false);

            return;
        }

        var page = await client.ConversationsAsync(cancellationToken: token).ConfigureAwait(false);

        var existing = page.Items
            .Where(c => !c.Closed && c.Item is null && !string.Equals(c.Id, source, StringComparison.Ordinal)
                && !c.Id.StartsWith("owns-", StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToList();

        var choosing = Prompting.Choose(Title, Describe(preview, source), [Stay, New, .. existing]);
        var opened = Prompting.Open(app, choosing);
        IReadOnlyList<long> all = [.. preview.Exchanges.Select(e => e.Id)];

        choosing.Done += (_, picked) =>
        {
            Prompting.Close(app, opened);

            if (picked is null or Stay)
            {
                _ = done("Nothing moved.", null, []);

                return;
            }

            if (!string.Equals(picked, New, StringComparison.Ordinal))
            {
                queue(moving => MoveAsync(client, source, picked, false, all, done, moving));

                return;
            }

            var naming = Prompting.Text(
                Title,
                "What is the new conversation called?\n\n" + ConversationName.Rule,
                problem: said => string.IsNullOrWhiteSpace(said) || ConversationName.Correct(said.Trim()) != said.Trim()
                    ? ConversationName.Rule
                    : null);

            var asked = Prompting.Open(app, naming);

            naming.Done += (_, name) =>
            {
                Prompting.Close(app, asked);

                if (string.IsNullOrWhiteSpace(name))
                {
                    _ = done("Nothing moved.", null, []);

                    return;
                }

                queue(moving => MoveAsync(client, source, name.Trim(), true, all, done, moving));
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
        Func<string, string?, IReadOnlyList<long>, Task> done,
        CancellationToken token)
    {
        var moved = await client.MoveExchangesAsync(source, destination, isNew, exchanges, token).ConfigureAwait(false);

        if (moved.Refused is { } refused)
        {
            await done($"Nothing moved: {refused}", null, []).ConfigureAwait(false);

            return;
        }

        await done(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Moved {moved.Exchanges} exchange{(moved.Exchanges == 1 ? string.Empty : "s")} ({moved.Messages} message{(moved.Messages == 1 ? string.Empty : "s")}) to {destination}."),
            destination,
            exchanges).ConfigureAwait(false);
    }

    /// <summary>The door: everything that would go, and what happens to it.</summary>
    private static string Describe(MovePreview preview, string source)
    {
        var said = new StringBuilder()
            .AppendLine("Each message keeps its words, its author and its time, and you can move them back afterwards. This takes:");

        foreach (var exchange in preview.Exchanges.Take(8))
        {
            said.Append(CultureInfo.InvariantCulture, $"- {Label(exchange)}{(exchange.Open ? " (still open)" : string.Empty)}");

            if (exchange.AddedBecause is { } because)
            {
                said.Append(" Added: ").Append(Safe.Text(because));
            }

            said.AppendLine();
        }

        if (preview.Exchanges.Count > 8)
        {
            said.Append(CultureInfo.InvariantCulture, $"- and {preview.Exchanges.Count - 8} more").AppendLine();
        }

        if (preview.Exchanges.Any(e => e.Open))
        {
            said.AppendLine("Work still open carries on in the new place. Agents aren't woken, and are told when they next collect.");
        }

        said.AppendLine("Anything an agent asked as a result stays behind unless you ticked it too.");

        if (preview.FilesStaying.Count > 0)
        {
            said.Append(CultureInfo.InvariantCulture, $"{preview.FilesStaying.Count} file(s) stay in {Safe.Text(source)}.").AppendLine();
        }

        return said.AppendLine().Append("Where do they go?").ToString();
    }

    private static string Label(MovePreviewExchange exchange) =>
        Safe.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"{exchange.Id}: {exchange.Requester.Value} to {exchange.Receiver.Value}: {exchange.FirstWords}"));
}
