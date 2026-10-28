using System.Globalization;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Applying a project's retention policy now, behind the project's name typed back.
/// </summary>
/// <remarks>
/// <para>
/// <b>It removes text permanently</b> (§9.4), so it takes the door removing a project takes: described, the
/// safe answer first, the name typed back. The policy is the broker's and nothing here chooses it — this
/// brings the next retention pass forward, and says what it removed and what the floor kept.
/// </para>
/// <para>
/// Every surface offers it: a terminal that could not would be the one surface where somebody who can see a
/// deployment's traffic cannot act on its retention.
/// </para>
/// </remarks>
internal static class Pruning
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Apply the retention policy now";

    private const string Keep = "Not now";

    private const string Prune = "Prune it (deletes spent message text permanently)";

    private const string Kept = "Nothing was removed.";

    private const string WrongName = "That is not this project's name. Nothing was removed.";

    private const string TypeItBack = "Type the project's name to confirm:";

    /// <summary>Opens the dialogs that prune a project.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="project">The project.</param>
    /// <param name="queue">Runs the operation, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        ProjectSettings project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var name = project.Name.Value;

        var described =
            $"""
             {Safe.Text(name)}

             Its broker applies its retention policy now rather than at its next retention pass. Message text past the
             policy is deleted permanently: export a conversation first if it is worth keeping. Nothing on an
             open exchange, and nothing not yet collected, is removed.
             """;

        var choosing = Prompting.Choose(Title, described, [Keep, Prune]);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Prune)
            {
                _ = done(Kept);

                return;
            }

            var typing = Prompting.Text(Title, TypeItBack);
            var typed = Prompting.Open(app, typing);

            typing.Done += (_, said) =>
            {
                Prompting.Close(app, typed);

                if (!string.Equals(said, name, StringComparison.Ordinal))
                {
                    _ = done(WrongName);

                    return;
                }

                queue(t => PruneAsync(client, t, done));
            };
        };

        return Task.CompletedTask;
    }

    /// <summary>Prunes, and says what went and what the floor kept.</summary>
    private static async Task PruneAsync(BrokerClient client, CancellationToken cancellationToken, Func<string, Task> done)
    {
        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.PruneAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(outcome.Problem!).ConfigureAwait(false);

            return;
        }

        var pruned = outcome.Value!;

        var said = string.Create(CultureInfo.InvariantCulture, $"Removed {pruned.Removed} message(s)");

        said += ".";

        if (pruned.HeldByFloor > 0)
        {
            said += string.Create(
                CultureInfo.InvariantCulture,
                $" {pruned.HeldByFloor} more were old enough and were kept, because they are on open work, not yet collected, or the request a result not yet collected carries back.");
        }

        if (pruned.Capped)
        {
            said += " It stopped at its own limit with more still to remove; run it again.";
        }

        await done(said).ConfigureAwait(false);
    }
}
