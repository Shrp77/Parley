using System.Globalization;
using Parley.Client;
using Parley.Client.Seating;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>Closes a Request waiting on the person without answering it (SPEC.md §10.2, <c>close_request</c>).</summary>
/// <remarks>
/// <c>CloseRequestAsync</c> lives here and nowhere else in this surface, carrying its door: the person picks which one,
/// reads what will happen and may say why. Nothing typed back, unlike closing a conversation: it ends one request, loses
/// no words, and the agent can ask again.
/// </remarks>
internal static class ClosingARequest
{
    /// <summary>The action's name, on the status line and in the menu.</summary>
    internal const string Title = "Close a request without answering";

    private const string Why =
        "Close it without answering. The conversation stays open, and they are told it was closed.\n\n"
        + "Reason (optional; they are told it):";

    /// <summary>The longest reason the broker keeps.</summary>
    private const int ReasonLimit = 1000;

    /// <summary>Opens the dialogs that close one.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="hold">The person's seat, which stops holding what is closed.</param>
    /// <param name="choices">What is waiting on the person.</param>
    /// <param name="queue">Runs the close off the loop.</param>
    /// <param name="done">Says what happened.</param>
    /// <returns>When the first dialog has been opened.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        SeatHold hold,
        IReadOnlyList<(string Label, long Exchange)> choices,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var choosing = Prompting.Choose(Title, "Which one?", [.. choices.Select(c => c.Label)]);
        var picking = Prompting.Open(app, choosing);

        choosing.Done += (_, picked) =>
        {
            Prompting.Close(app, picking);

            var chosen = choices.FirstOrDefault(c => c.Label == picked);

            if (chosen.Label is null)
            {
                _ = done(string.Empty);

                return;
            }

            var asking = Prompting.Text(
                Title,
                Why,
                problem: said => said.Length > ReasonLimit
                    ? string.Create(CultureInfo.InvariantCulture, $"At most {ReasonLimit} characters.")
                    : null);

            var asked = Prompting.Open(app, asking);

            asking.Done += (_, reason) =>
            {
                Prompting.Close(app, asked);

                if (reason is null)
                {
                    _ = done("Left open.");

                    return;
                }

                queue(token => CloseAsync(
                    client, hold, chosen.Exchange, string.IsNullOrWhiteSpace(reason) ? null : reason, token, done));
            };
        };

        return Task.CompletedTask;
    }

    /// <summary>Closes it and says what happened.</summary>
    private static async Task CloseAsync(
        BrokerClient client,
        SeatHold hold,
        long exchange,
        string? reason,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.CloseRequestAsync(exchange, reason, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(outcome.Problem!).ConfigureAwait(false);

            return;
        }

        if (outcome.Value)
        {
            hold.Forget(exchange);
        }

        await done(outcome.Value ? "Closed, and they have been told." : "That request is not open, so nothing changed.")
            .ConfigureAwait(false);
    }
}
