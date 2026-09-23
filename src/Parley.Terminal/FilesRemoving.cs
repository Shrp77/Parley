using System.Globalization;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Removing files from the location, whichever agent placed them, behind a description and a deliberate answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one file in this surface that calls <c>RemoveFilesAsync</c></b>, and it carries the door: which files, how
/// much space, who placed them, that every agent that fetched one is told, and that it cannot be undone — with keeping
/// them the answer under the cursor. <c>SurfaceTests</c> holds the call to this file and the door to its text.
/// </para>
/// <para>
/// A choice rather than a name typed back, which is the owner's rule for removal on every surface: the typed name is
/// kept for pruning, which removes everything.
/// </para>
/// </remarks>
internal static class FilesRemoving
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Remove files";

    private const string Keep = "Keep them";

    private const string Remove = "Remove them (deletes them for everybody; cannot be undone)";

    private const string Kept = "Nothing was removed.";

    /// <summary>Describes the files and asks.</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for the project.</param>
    /// <param name="files">The files to remove.</param>
    /// <param name="queue">Runs the removal, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        IReadOnlyList<SharedFile> files,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var described =
            "These files will be removed, whichever agent placed them. Every agent that fetched one is told it is " +
            "gone. It cannot be undone.\n\n" +
            string.Join('\n', files.Select(Files.Row)) +
            "\n\n" +
            string.Create(CultureInfo.InvariantCulture, $"{files.Count} file(s), {Files.Size(files.Sum(f => f.SizeBytes))}.");

        var choosing = Prompting.Choose(Title, described, [Keep, Remove]);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Remove)
            {
                _ = done(Kept);
                return;
            }

            queue(t => RemoveAsync(client, files, t, done));
        };

        return Task.CompletedTask;
    }

    private static async Task RemoveAsync(
        BrokerClient client,
        IReadOnlyList<SharedFile> files,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var references = files.Select(f => new FileRef(f.ConversationId, f.Name.Value)).ToList();

        var read = await BrokerDirectory.ReadAsync(
            async () => await client.RemoveFilesAsync(references, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        if (!read.Reachable)
        {
            await done(read.Problem!).ConfigureAwait(false);
            return;
        }

        var outcome = read.Value!;

        var said = string.Create(
            CultureInfo.InvariantCulture,
            $"Removed {outcome.Removed.Count} file(s), freeing {Files.Size(outcome.BytesFreed)}.");

        foreach (var refused in outcome.Refused)
        {
            said += " " + Safe.Text($"{refused.File.Name}: {refused.Detail ?? refused.Rejection.ToString()}");
        }

        await done(said).ConfigureAwait(false);
    }
}
