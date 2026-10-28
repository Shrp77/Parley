using System.Globalization;
using Parley.Client;
using Parley.Client.Files;
using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// A project's or a conversation's files, and what the person can do with each (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>A list the person chooses from, built on the one choice dialog every door here already uses</b>, so it scrolls,
/// sizes its description and puts the safe answer first like every other. Choosing a file offers opening it to read
/// and removing it; the list also offers placing a file (<see cref="FilesPlacing"/>), the folder and removing every
/// file. A file copied into the folder by hand is not in Parley and has no row. The two that delete go through their own
/// files, <see cref="FilesRemoving"/> and <see cref="FilesPruning"/>, which are the only places the terminal calls
/// them — the rule <c>SurfaceTests</c> holds every operator operation here to.
/// </para>
/// <para>
/// <b>Every page is read before the list is shown.</b> The broker bounds each call; a person scrolling a list should
/// see all of it rather than a page and a way to ask for more.
/// </para>
/// <para>
/// Opening launches on this machine only when the broker's machine is this one (<see cref="LocalLauncher"/>), and the
/// launcher is the shell's seam so no test run opens an editor.
/// </para>
/// </remarks>
internal static class Files
{
    /// <summary>What the action on a project is called.</summary>
    internal const string ProjectTitle = "Files in this project";

    /// <summary>What the action on a conversation is called.</summary>
    internal const string ConversationTitle = "Files in this conversation";

    private const string Close = "Close";

    private const string OpenFolder = "Open the folder";

    private const string RemoveEvery = "Remove every file…";

    private const string Back = "Back";

    private const string OpenIt = "Open it";

    private const string RemoveIt = "Remove it…";

    /// <summary>Reads every file and opens the list.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for the project.</param>
    /// <param name="project">The project.</param>
    /// <param name="conversation">One conversation's files, or <see langword="null"/> for every one.</param>
    /// <param name="launcher">Opens files and folders on this machine.</param>
    /// <param name="queue">Runs work, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when something is over.</param>
    /// <returns>When the read is queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        ProjectSettings project,
        string? conversation,
        LocalLauncher launcher,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        queue(async cancellationToken =>
        {
            var read = await BrokerDirectory.ReadAsync(
                () => EveryFileAsync(client, conversation, cancellationToken)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done(read.Problem!).ConfigureAwait(false);
                return;
            }

            var (files, placements, notListed) = read.Value!;

            Show(app, client, project, conversation, launcher, queue, done, files, placements, notListed);
        });

        return Task.CompletedTask;
    }

    /// <summary>What the list says above its rows: where, how much, and who placed how much.</summary>
    /// <param name="project">The project's name.</param>
    /// <param name="conversation">The conversation, or <see langword="null"/>.</param>
    /// <param name="files">The files.</param>
    /// <param name="placements">The placement counts.</param>
    /// <param name="notListed">Files in the folders that nothing placed, and so have no row.</param>
    /// <returns>The description, safe to draw.</returns>
    internal static string Describe(
        string project,
        string? conversation,
        IReadOnlyList<SharedFile> files,
        IReadOnlyList<FilePlacementCount> placements,
        int notListed = 0)
    {
        var lines = new List<string>
        {
            files.Count == 0
                ? "The file location is empty."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{files.Count} file(s), {Size(files.Sum(f => f.SizeBytes))}, in {(conversation is null ? project : conversation)}.")
        };

        foreach (var total in files.GroupBy(f => f.ConversationId, StringComparer.Ordinal))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {total.Key}: {total.Count()} file(s), {Size(total.Sum(f => f.SizeBytes))}"));
        }

        // The rows' times are local, as the transcript's are, and the zone is said once here rather than on every row.
        if (files.Count > 0)
        {
            lines.Add(LocalTime.Note());
        }

        if (SharedFileListing.NotListedSentenceFor(notListed) is { } sentence)
        {
            lines.Add(sentence);
        }

        if (placements.Count > 0)
        {
            lines.Add("Placed so far:");
            lines.AddRange(placements.Select(p => string.Create(
                CultureInfo.InvariantCulture, $"  {p.Agent}: {p.Placements} placement(s), {Size(p.Bytes)}")));
        }

        // Lines rather than Text: the breaks are this surface's layout, and Text draws each as a replacement character.
        return Safe.Lines(string.Join('\n', lines));
    }

    /// <summary>One file's row: conversation and name, size, short hash, placer and when it was placed.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The row, safe to draw.</returns>
    /// <remarks>
    /// Indented, so that only this surface's own words sit at column zero in a list of somebody else's text — the rule
    /// the transcript and the folder picker keep.
    /// </remarks>
    internal static string Row(SharedFile file) =>
        "  " + Safe.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"{file.ConversationId}/{file.Name.Value}  {Size(file.SizeBytes)}  {file.Sha256.Short}  " +
            $"{file.PlacedBy?.Value ?? "the person"}  {LocalTime.Write(file.PlacedUtc)}"));

    /// <summary>A size in the units a person reads.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size, with its unit.</returns>
    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB")
    };

    private static void Show(
        IApplication app,
        BrokerClient client,
        ProjectSettings project,
        string? conversation,
        LocalLauncher launcher,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        IReadOnlyList<SharedFile> files,
        IReadOnlyList<FilePlacementCount> placements,
        int notListed)
    {
        var rows = files.Select(Row).ToList();
        var choices = new List<string> { Close, FilesPlacing.Choice, OpenFolder };

        if (files.Count > 0)
        {
            choices.Add(RemoveEvery);
        }

        choices.AddRange(rows);

        var choosing = Prompting.Choose(
            conversation is null ? ProjectTitle : ConversationTitle,
            Describe(project.Name.Value, conversation, files, placements, notListed),
            choices);

        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen is null || chosen == Close)
            {
                return;
            }

            if (chosen == FilesPlacing.Choice)
            {
                _ = FilesPlacing.OpenAsync(app, client, conversation, queue, done);
                return;
            }

            if (chosen == OpenFolder)
            {
                queue(t => FolderAsync(client, conversation, launcher, t, done));
                return;
            }

            if (chosen == RemoveEvery)
            {
                _ = FilesPruning.OpenAsync(app, client, project, files.Count, files.Sum(f => f.SizeBytes), queue, done);
                return;
            }

            var index = rows.IndexOf(chosen);

            if (index < 0)
            {
                return;
            }

            var file = files[index];

            var acting = Prompting.Choose(
                Safe.Text(file.Name.Value),
                Row(file).TrimStart(),
                [Back, OpenIt, RemoveIt]);

            var actingToken = Prompting.Open(app, acting);

            acting.Done += (_, action) =>
            {
                Prompting.Close(app, actingToken);

                if (action == OpenIt)
                {
                    queue(t => OpenFileAsync(client, file, launcher, t, done));
                }
                else if (action == RemoveIt)
                {
                    _ = FilesRemoving.OpenAsync(app, client, [file], queue, done);
                }
            };
        };
    }

    private static async Task<(IReadOnlyList<SharedFile> Files, IReadOnlyList<FilePlacementCount> Placements, int NotListed)> EveryFileAsync(
        BrokerClient client,
        string? conversation,
        CancellationToken cancellationToken)
    {
        var files = new List<SharedFile>();
        IReadOnlyList<FilePlacementCount> placements = [];
        var notListed = 0;
        string? next = null;

        do
        {
            var listing = await client.ListSharedFilesAsync(conversation, FileLimits.MaximumPageSize, next, cancellationToken)
                .ConfigureAwait(false);

            files.AddRange(listing.Page.Files);
            placements = listing.Placements;
            notListed = listing.NotListed;
            next = listing.Page.NextPageToken;
        }
        while (next is not null);

        return (files, placements, notListed);
    }

    private static async Task OpenFileAsync(
        BrokerClient client,
        SharedFile file,
        LocalLauncher launcher,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var read = await BrokerDirectory.ReadAsync(
            () => client.DescribeFileLocationAsync(file.ConversationId, file.Name.Value, cancellationToken)).ConfigureAwait(false);

        await done(!read.Reachable
            ? read.Problem!
            : read.Value!.Location is { } location
                ? Safe.Text(launcher.OpenFile(location).Said)
                : Safe.Text(read.Value.Refusal?.Detail ?? "That file is not in the location any more.")).ConfigureAwait(false);
    }

    private static async Task FolderAsync(
        BrokerClient client,
        string? conversation,
        LocalLauncher launcher,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var read = await BrokerDirectory.ReadAsync(
            () => client.DescribeFileLocationAsync(conversation, null, cancellationToken)).ConfigureAwait(false);

        await done(!read.Reachable
            ? read.Problem!
            : read.Value!.Location is { } location
                ? Safe.Text(launcher.OpenFolder(location).Said)
                : Safe.Text(read.Value.Refusal?.Detail ?? "That conversation has no files.")).ConfigureAwait(false);
    }
}
