using System.Globalization;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Cli.Top;

/// <summary>
/// Destroying one conversation, behind the door a purge stands behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Described, keep offered first, the conversation's own identifier typed back.</b> The same
/// shape as <see cref="Withdrawing"/> and for the same reason: SPEC.md §14 argues that a deletion
/// happens inside the system or it happens in a SQL client with no ordering and no report, and the
/// surface's part of that bargain is that nobody reaches it by accident.
/// </para>
/// <para>
/// <b>The waiting asker is named before the identifier is asked for, not after.</b> An open
/// exchange in a destroyed conversation does not orphan and is not ghosted -- it ceases to exist,
/// so the agent waiting on it is never told anything at all, not even the ghost §7 guarantees for a
/// question nobody answers. That is a different outcome from every other way an exchange can end
/// and the one an operator is least likely to predict, so it is put in front of them while they
/// can still decline.
/// </para>
/// </remarks>
internal static class Destroying
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Destroy this conversation";

    /// <summary>The choice that does nothing, offered first.</summary>
    internal const string Keep = "Keep it";

    /// <summary>The choice that destroys it.</summary>
    internal const string Destroy = "Destroy it (deletes every message, for every participant; permanent)";

    /// <summary>What is said when nothing was done.</summary>
    internal const string Kept = "Kept.";

    /// <summary>What is said when the typed identifier was not the right one.</summary>
    internal const string WrongName =
        "That is not this conversation's identifier. Nothing changed.";

    /// <summary>What the person is asked to type.</summary>
    internal const string TypeItBack = "Type the conversation's identifier to confirm:";

    /// <summary>What is said before the identifier is asked for, when something is waiting.</summary>
    internal const string Waiting =
        "An exchange in this conversation is still open. Destroying it does not orphan that "
        + "exchange and does not ghost it: the exchange stops existing, so whoever is waiting is "
        + "never told anything at all.";

    /// <summary>What the confirmation of that consequence is called.</summary>
    internal const string ConsequenceTitle = "Somebody is waiting on this";

    /// <summary>Accepting the consequence.</summary>
    private const string Accept = "I understand; continue";

    /// <summary>Opens the dialog that destroys a conversation.</summary>
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

        var choosing = Prompting.Choose(Title, Describe(row), [Keep, Destroy]);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Destroy)
            {
                _ = done(Kept);

                return;
            }

            if (row.HasOpenExchange)
            {
                Consequence(app, client, row, queue, done);

                return;
            }

            Name(app, row, done, () => queue(t => DestroyAsync(client, row, t, done)));
        };

        return Task.CompletedTask;
    }

    /// <summary>What the person is shown before anything is asked of them.</summary>
    /// <param name="row">The conversation.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// Everything that will be lost, in the words the list already used for it, so somebody
    /// checking they have the right conversation is comparing like with like.
    /// </remarks>
    internal static string Describe(ConversationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var participants = row.Participants.Count == 0
            ? "nobody"
            : string.Join(", ", row.Participants.Select(p => Safe.Text(p.Value)));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             {Safe.Text(row.Id)}

             {row.Messages} message{(row.Messages == 1 ? string.Empty : "s")}, between {participants}.

             Every participant's half goes with it, including peers who did nothing but take part.
             That is what deleting a conversation means: one with a side removed is a record that
             misleads whoever reads it next. It cannot be undone.
             """);
    }

    /// <summary>Names the waiting asker's outcome, and takes the acceptance of it.</summary>
    private static void Consequence(
        IApplication app,
        BrokerClient client,
        ConversationRow row,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var choosing = Prompting.Choose(ConsequenceTitle, Waiting, [Keep, Accept]);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Accept)
            {
                _ = done(Kept);

                return;
            }

            Name(app, row, done, () => queue(t => DestroyAsync(client, row, t, done)));
        };
    }

    /// <summary>Takes the identifier typed back, and runs the destroy only on an exact match.</summary>
    /// <remarks>
    /// Ordinal, and the whole string. A comparison that trimmed or ignored case would accept a
    /// near-miss, and the point of asking is that somebody has read what is in front of them.
    /// </remarks>
    private static void Name(
        IApplication app, ConversationRow row, Func<string, Task> done, Action then)
    {
        var typing = Prompting.Text(Title, TypeItBack);
        var token = Prompting.Open(app, typing);

        typing.Done += (_, typed) =>
        {
            Prompting.Close(app, token);

            if (string.Equals(typed, row.Id, StringComparison.Ordinal))
            {
                then();
            }
            else
            {
                _ = done(WrongName);
            }
        };
    }

    /// <summary>Destroys it and reports what went, per table.</summary>
    private static async Task DestroyAsync(
        BrokerClient client,
        ConversationRow row,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.DestroyAsync(row.Id, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(outcome.Problem!).ConfigureAwait(false);

            return;
        }

        var destroyed = outcome.Value!;

        if (destroyed.Verdict == DestroyVerdict.NoSuchConversation)
        {
            await done($"There is no conversation called {Safe.Text(row.Id)} any more.")
                .ConfigureAwait(false);

            return;
        }

        // Per table rather than as a total, because a total is the one number that carries no
        // warning -- and this report is the only account of those rows that will ever exist.
        var toll = destroyed.Destroyed.Count == 0
            ? "nothing else"
            : string.Join(", ", destroyed.Destroyed.Select(t => $"{t.Rows} {t.Table}"));

        await done($"Destroyed {Safe.Text(row.Id)}, and with it {toll}.").ConfigureAwait(false);
    }
}
