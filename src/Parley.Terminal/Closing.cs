using System.Globalization;
using Parley.Client;
using Parley.Client.Views;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Closing one conversation now, whatever its participants have voted, behind the door destroying one stands behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing times out, so this is how a person ends work that has stopped</b> (principle 2.8). It is an
/// operator operation (§10.2), named <c>force_conclude</c> rather than <c>conclude</c> so no adapter binds it by
/// accident, and the owner's parity decision brought it here: a terminal that shows stalled work and cannot end
/// it sends the person to another surface to do the one thing the flag asked of them.
/// </para>
/// <para>
/// <b>Described, leave-it-open offered first, a reason asked for, the identifier typed back.</b> It deletes
/// nothing, so it is a smaller act than <see cref="Destroying"/>; but it ends somebody's work over their vote, and
/// the reason is what each participant holding that work is told.
/// </para>
/// </remarks>
internal static class Closing
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Close this conversation now";

    private const string Keep = "Leave it open";

    private const string Close = "Close it (every request still open in it is closed; each participant holding one is told)";

    private const string Kept = "Left open.";

    private const string WrongName = "That is not this conversation's identifier. Nothing changed.";

    private const string TypeItBack = "Type the conversation's identifier to confirm:";

    private const string Why =
        "Reason (optional; each participant holding open work in it is told):";

    /// <summary>The longest reason the broker keeps.</summary>
    private const int ReasonLimit = 1000;

    /// <summary>Opens the dialogs that close a conversation.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="row">The conversation as the region drew it.</param>
    /// <param name="queue">Runs the operation, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        ConversationRow row,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var choosing = Prompting.Choose(Title, Describe(row), [Keep, Close]);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Close)
            {
                _ = done(Kept);

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
                    _ = done(Kept);

                    return;
                }

                var typing = Prompting.Text(Title, TypeItBack);
                var typed = Prompting.Open(app, typing);

                typing.Done += (_, name) =>
                {
                    Prompting.Close(app, typed);

                    // Ordinal and whole, for the reason Destroying gives: the point of asking is that
                    // somebody has read what is in front of them.
                    if (!string.Equals(name, row.Id, StringComparison.Ordinal))
                    {
                        _ = done(WrongName);

                        return;
                    }

                    queue(t => CloseAsync(client, row, string.IsNullOrWhiteSpace(reason) ? null : reason, t, done));
                };
            };
        };

        return Task.CompletedTask;
    }

    /// <summary>What the person is shown before anything is asked of them.</summary>
    /// <param name="row">The conversation.</param>
    /// <returns>The description.</returns>
    private static string Describe(ConversationRow row)
    {
        var participants = row.Participants.Count == 0
            ? "nobody"
            : string.Join(", ", row.Participants.Select(p => Safe.Text(p.Value)));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             {Safe.Text(row.Id)}

             {row.Messages} message{(row.Messages == 1 ? string.Empty : "s")}, between {participants}.

             It is closed now, whatever its participants have voted. Every request still open in it is closed
             with a result saying a person closed it, and each participant holding that work is told. A new
             request naming it is refused.

             {Parley.Client.Rendering.RetentionWords.WhatClosingKeeps}
             """);
    }

    /// <summary>Closes it and says what happened.</summary>
    private static async Task CloseAsync(
        BrokerClient client,
        ConversationRow row,
        string? reason,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.ForceConcludeAsync(row.Id, reason, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(outcome.Problem!).ConfigureAwait(false);

            return;
        }

        await done(outcome.Value
            ? $"Closed {Safe.Text(row.Id)}."
            : $"{Safe.Text(row.Id)} is not open, so nothing changed.").ConfigureAwait(false);
    }
}
