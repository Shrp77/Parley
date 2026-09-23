using System.Globalization;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Removing every file in a project's location, behind the project's name typed back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one file in this surface that calls <c>PruneFilesAsync</c></b>, and it takes the door pruning messages takes:
/// described, the safe answer first, and the project's name typed back — because it removes every file every agent
/// placed, for good. <c>SurfaceTests</c> holds the call to this file and the typed name to its text.
/// </para>
/// <para>
/// Named apart from <see cref="Pruning"/>, which applies the retention policy to messages and never touches a file.
/// </para>
/// </remarks>
internal static class FilesPruning
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Remove every file";

    private const string Keep = "Keep them";

    private const string Prune = "Remove every file (cannot be undone)";

    private const string Kept = "Nothing was removed.";

    private const string WrongName = "That is not this project's name. Nothing was removed.";

    private const string TypeItBack = "Type the project's name to confirm:";

    /// <summary>Describes what goes, asks, and takes the project's name typed back.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for the project.</param>
    /// <param name="project">The project.</param>
    /// <param name="files">How many files the location holds.</param>
    /// <param name="bytes">How many bytes they come to.</param>
    /// <param name="queue">Runs the prune, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        ProjectSettings project,
        int files,
        long bytes,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var name = project.Name.Value;

        var described = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             {Safe.Text(name)}

             Every file in its location will be removed: {files} file(s), {Files.Size(bytes)}. Conversations, messages
             and agents are untouched. Every agent that fetched a file is told it is gone. It cannot be undone.
             """);

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

                queue(t => RemoveEveryFileAsync(client, t, done));
            };
        };

        return Task.CompletedTask;
    }

    private static async Task RemoveEveryFileAsync(BrokerClient client, CancellationToken cancellationToken, Func<string, Task> done)
    {
        var read = await BrokerDirectory.ReadAsync(
            async () => await client.PruneFilesAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        await done(read.Reachable
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Removed {read.Value!.FilesRemoved} file(s), freeing {Files.Size(read.Value.BytesFreed)}.")
            : read.Problem!).ConfigureAwait(false);
    }
}
