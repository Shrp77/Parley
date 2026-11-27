using System.Globalization;
using Parley.Client;
using Parley.Client.Files;
using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// A conversation's Media: its images, then its other files, each newest first (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The web console's Media view, as a terminal can draw it.</b> A terminal cannot draw an image, so this lists them and
/// says so, and choosing one opens it in this machine's own program: in place on the broker's machine, or a copy read
/// through the broker anywhere else (<see cref="Files.OpenFileAsync"/>).
/// </para>
/// <para>
/// Only for reading. Removing a file stays in <see cref="Files"/>, behind its own door.
/// </para>
/// </remarks>
internal static class Media
{
    /// <summary>What the action is called.</summary>
    internal const string Title = "Media in this conversation";

    /// <summary>What the list says about images, since it cannot show them.</summary>
    internal const string CannotDraw = "A terminal cannot draw images. Choose one to open it in this machine's own program.";

    private const string Close = "Close";

    /// <summary>Reads the conversation's files and opens the list.</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for the project.</param>
    /// <param name="conversation">The conversation.</param>
    /// <param name="launcher">Opens files on this machine.</param>
    /// <param name="queue">Runs work, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when something is over.</param>
    /// <param name="inView">
    /// Whether the project read is still the one in view when the read lands, or null for always. Conversation names repeat
    /// across projects, so a list opened over another project reads as that one's.
    /// </param>
    /// <returns>When the read is queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        string conversation,
        LocalLauncher launcher,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        Func<bool>? inView = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        queue(async cancellationToken =>
        {
            var read = await BrokerDirectory.ReadAsync(
                () => Placements.EveryAsync(client, conversation, cancellationToken)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done(read.Problem!).ConfigureAwait(false);
                return;
            }

            if (inView is not null && !inView())
            {
                return;
            }

            Show(app, client, conversation, launcher, queue, done, read.Value!);
        });

        return Task.CompletedTask;
    }

    /// <summary>What the list says above its rows.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="images">Its images.</param>
    /// <param name="others">Its other files.</param>
    /// <returns>The description, safe to draw.</returns>
    internal static string Describe(string conversation, IReadOnlyList<SharedFile> images, IReadOnlyList<SharedFile> others)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(others);

        var lines = new List<string>
        {
            images.Count + others.Count == 0
                ? "There are no files in this conversation."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{images.Count} image(s) and {others.Count} other file(s) in {conversation}, newest first."),
        };

        if (images.Count > 0)
        {
            lines.Add(CannotDraw);
        }

        if (images.Count + others.Count > 0)
        {
            lines.Add(LocalTime.Note());
        }

        return Safe.Lines(string.Join('\n', lines));
    }

    /// <summary>One file's row: its kind, name, size, placer and when.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The row, indented and safe to draw.</returns>
    internal static string Row(SharedFile file) =>
        "  " + Safe.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"[{Placements.Kind(file)}] {file.Name.Value}  {Placements.Size(file.SizeBytes)}  {Placements.Placer(file)}  {LocalTime.Write(file.PlacedUtc)}"));

    private static void Show(
        IApplication app,
        BrokerClient client,
        string conversation,
        LocalLauncher launcher,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        IReadOnlyList<SharedFile> files)
    {
        var (images, others) = Placements.Media(files);
        var listed = images.Concat(others).ToList();
        var rows = listed.Select(Row).ToList();

        var choosing = Prompting.Choose(Title, Describe(conversation, images, others), [Close, .. rows]);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            var index = chosen is null ? -1 : rows.IndexOf(chosen);

            if (index < 0)
            {
                return;
            }

            var file = listed[index];

            queue(t => Files.OpenFileAsync(client, file, launcher, t, done));
        };
    }
}
