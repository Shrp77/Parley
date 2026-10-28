using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Files;
using Parley.Core;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// A project's file location: what is in it, and the person's five operations on it (SPEC.md §9.5, §10.2). The
/// problems sweeps found are on their own page, <see cref="FileProblemsModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placing is how the person hands an agent a file.</b> A file copied into a conversation's folder by hand is never
/// listed and the next sweep deletes it (§9.5), so the page takes an upload and places it as the person, into a conversation the project lists. The
/// browser posts the whole file in one request, which is why this page, alone, bounds a request's size
/// (<see cref="PlaceLimitBytes"/>).
/// </para>
/// <para>
/// <b>Removing and pruning each describe what will go before anything goes.</b> Removal lists the files, their sizes
/// and who placed them, and takes a second press; pruning says how many files and how much space, and takes the
/// project's name typed back — the door this console already puts in front of destroying a conversation, because
/// both delete somebody else's work for good.
/// </para>
/// <para>
/// <b>Opening launches on the machine running this console, and only there.</b> The broker says which machine holds
/// the location; a console viewed from another machine's browser opens files where the console runs, or says where
/// they are when that is not the broker's machine. Every action is a POST, so a link or an
/// image elsewhere cannot open, remove or prune anything, and Razor Pages refuses a POST without this page's
/// antiforgery token.
/// </para>
/// <para>
/// Names are other participants' text and are HTML-encoded by Razor like everything else a page renders.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
[RequestSizeLimit(PlaceLimitBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = PlaceLimitBytes)]
public sealed class FilesModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The largest request this page accepts, and so the largest file it can place.</summary>
    /// <remarks>
    /// <para>
    /// <b>Not the message cap.</b> The 8,000 characters bound what an agent reads; a placed file's bytes never reach a
    /// model's context, and SPEC.md sets no size for a file. This bound is the console's own, on one browser upload.
    /// </para>
    /// <para>
    /// Set here rather than left to the server's default, which is 30 MB on Kestrel and none at all elsewhere, so what
    /// the console accepts does not depend on which server hosts it. 256 MiB is far past any <c>.md</c> or <c>.txt</c>
    /// a person hands over; the command line and the terminal stream from disk and have no bound but the broker's
    /// disk.
    /// </para>
    /// </remarks>
    public const long PlaceLimitBytes = 256L * 1024 * 1024;

    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>One conversation's files only, when set.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Conversation { get; set; }

    /// <summary>The page to show, as the previous page's token.</summary>
    [BindProperty(SupportsGet = true)]
    public string? PageToken { get; set; }

    /// <summary>The page being shown.</summary>
    public SharedFileListing? Listing { get; private set; }

    /// <summary>The files a removal will take, while it waits to be confirmed.</summary>
    public IReadOnlyList<SharedFile> ConfirmingRemoval { get; private set; } = [];

    /// <summary>Whether a prune is waiting for the project's name.</summary>
    public bool ConfirmingPrune { get; private set; }

    /// <summary>The conversations a file can be placed in: the one in view, or every one the project lists.</summary>
    public IReadOnlyList<string> Placeable { get; private set; } = [];

    /// <summary>What happened, once something has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Why the page could not be read, if it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>A size in the units a person reads.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size, with its unit.</returns>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB")
    };

    /// <summary>Shows a page of the location.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Places an uploaded file in a conversation, as the person.</summary>
    /// <param name="upload">The file.</param>
    /// <param name="placeIn">The conversation.</param>
    /// <param name="placeAs">The name to place it under, or empty for the file's own name.</param>
    /// <returns>The page, saying what came of it.</returns>
    /// <remarks>
    /// Nothing to describe first: placing removes nothing and never changes a file already there, because a name that
    /// already holds other content is refused (§9.5). Only a conversation the project lists is accepted, so a field
    /// edited in the browser cannot name one this page did not offer.
    /// </remarks>
    public async Task<IActionResult> OnPostPlaceAsync(
        [FromForm] IFormFile? upload, [FromForm] string? placeIn, [FromForm] string? placeAs)
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        await LoadAsync(project).ConfigureAwait(false);

        if (upload is null)
        {
            Outcome = "No file was chosen. Nothing was placed.";
            return Page();
        }

        if (placeIn is null || !Placeable.Contains(placeIn, StringComparer.Ordinal))
        {
            Outcome = "Choose a conversation this project has. Nothing was placed.";
            return Page();
        }

        var name = string.IsNullOrWhiteSpace(placeAs) ? Path.GetFileName(upload.FileName) : placeAs.Trim();

        if (name.Length == 0)
        {
            Outcome = "The file has no name to place it under. Give it one. Nothing was placed.";
            return Page();
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var content = upload.OpenReadStream();

        await using (content.ConfigureAwait(false))
        {
            var placed = await BrokerDirectory.ReadAsync(
                () => broker.PlacePersonsFileAsync(placeIn, name, content, token)).ConfigureAwait(false);

            Outcome = placed.Reachable ? PlacementSaid.Of(placed.Value!, name, Size) : placed.Problem;
        }

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Describes a removal, or makes it once confirmed.</summary>
    /// <param name="selected">The files chosen, each as its conversation and name separated by '/'.</param>
    /// <param name="confirmed">"yes" once the description has been shown and the person pressed again.</param>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnPostRemoveAsync([FromForm] string[]? selected, [FromForm] string? confirmed)
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var references = (selected ?? [])
            .Select(value => value.Split('/', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new FileRef(parts[0], parts[1]))
            .Distinct()
            .ToList();

        if (references.Count == 0)
        {
            Outcome = "No files were chosen. Nothing was removed.";
            await LoadAsync(project).ConfigureAwait(false);
            return Page();
        }

        if (references.Count > FileLimits.NamesPerRemoval)
        {
            Outcome = $"At most {FileLimits.NamesPerRemoval} files can be removed at once. Nothing was removed.";
            await LoadAsync(project).ConfigureAwait(false);
            return Page();
        }

        if (!string.Equals(confirmed, "yes", StringComparison.Ordinal))
        {
            var everything = await BrokerDirectory.ReadAsync(
                () => EveryFileAsync(broker, token)).ConfigureAwait(false);

            if (!everything.Reachable)
            {
                Problem = everything.Problem;
                return Page();
            }

            ConfirmingRemoval = [.. everything.Value!.Where(file => references.Any(r =>
                string.Equals(r.ConversationId, file.ConversationId, StringComparison.Ordinal)
                && string.Equals(SharedFileName.KeyOf(r.Name), file.Name.Key, StringComparison.Ordinal)))];

            if (ConfirmingRemoval.Count == 0)
            {
                Outcome = "None of those files is in the location any more. Nothing was removed.";
                await LoadAsync(project).ConfigureAwait(false);
            }

            return Page();
        }

        var removed = await BrokerDirectory.ReadAsync(async () =>
            await broker.RemoveFilesAsync(references, token).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = removed.Reachable ? Describe(removed.Value!) : removed.Problem;

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Describes a prune, or makes it once the project's name has been typed back.</summary>
    /// <param name="typed">What the person typed, or <see langword="null"/> before the description was shown.</param>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnPostPruneAsync([FromForm] string? typed)
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        await LoadAsync(project).ConfigureAwait(false);

        if (typed is null)
        {
            ConfirmingPrune = Listing is { Page.TotalFiles: > 0 };
            Outcome = ConfirmingPrune ? null : "The file location is empty. There is nothing to prune.";
            return Page();
        }

        if (!string.Equals(typed, project.Name.Value, StringComparison.Ordinal))
        {
            Outcome = "That is not this project's name. Nothing was removed.";
            return Page();
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var pruned = await BrokerDirectory.ReadAsync(async () =>
            await broker.PruneFilesAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = pruned.Reachable
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Removed {pruned.Value!.FilesRemoved} file(s), freeing {Size(pruned.Value.BytesFreed)}. Every agent that fetched one will be told it is gone.")
            : pruned.Problem;

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Opens one file to read, on this machine.</summary>
    /// <param name="file">The file's name.</param>
    /// <param name="inConversation">Its conversation.</param>
    /// <returns>The page, saying what happened.</returns>
    /// <remarks>
    /// From the query by choice: each file's button carries its own file in its action, so one form serves every row.
    /// Opening changes nothing and opens only a <c>.md</c> or <c>.txt</c> file the broker holds, and a POST still needs
    /// the page's antiforgery token.
    /// </remarks>
    public async Task<IActionResult> OnPostOpenAsync([FromQuery] string? file, [FromQuery] string? inConversation)
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var located = await BrokerDirectory.ReadAsync(async () =>
            await broker.DescribeFileLocationAsync(inConversation, file, token).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = !located.Reachable
            ? located.Problem
            : located.Value!.Location is { } location
                ? Launcher.OpenFile(location).Said
                : located.Value.Refusal?.Detail ?? "That file is not in the location.";

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Opens the folder of the conversation being viewed, or the whole location, on this machine.</summary>
    /// <returns>The page, saying what happened.</returns>
    public async Task<IActionResult> OnPostFolderAsync()
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;
        var scope = string.IsNullOrWhiteSpace(Conversation) ? null : Conversation;

        var located = await BrokerDirectory.ReadAsync(async () =>
            await broker.DescribeFileLocationAsync(scope, null, token).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = !located.Reachable
            ? located.Problem
            : located.Value!.Location is { } location
                ? Launcher.OpenFolder(location).Said
                : located.Value.Refusal?.Detail ?? "That conversation has no files.";

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>The launcher: the one the host registered, which a test replaces, or the real one.</summary>
    private LocalLauncher Launcher =>
        HttpContext.RequestServices.GetService(typeof(LocalLauncher)) as LocalLauncher ?? LocalLauncher.ForThisMachine;

    private async Task LoadAsync(ProjectSettings project)
    {
        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;
        var scope = string.IsNullOrWhiteSpace(Conversation) ? null : Conversation;
        var page = string.IsNullOrWhiteSpace(PageToken) ? null : PageToken;

        var read = await BrokerDirectory.ReadAsync(async () =>
            await broker.ListSharedFilesAsync(scope, 0, page, token).ConfigureAwait(false)).ConfigureAwait(false);

        Listing = read.Value;
        Problem ??= read.Problem;

        if (scope is not null)
        {
            Placeable = [scope];
            return;
        }

        var conversations = await BrokerDirectory.ReadAsync(
            () => EveryConversationAsync(broker, token)).ConfigureAwait(false);

        Placeable = conversations.Value ?? [];
        Problem ??= conversations.Problem;
    }

    private static async Task<IReadOnlyList<string>> EveryConversationAsync(BrokerClient broker, CancellationToken token)
    {
        var ids = new List<string>();
        string? next = null;

        do
        {
            var page = await broker.ConversationsAsync(pageToken: next, cancellationToken: token).ConfigureAwait(false);

            ids.AddRange(page.Items.Select(c => c.Id));
            next = page.NextPageToken;
        }
        while (next is not null);

        return ids;
    }

    private static async Task<IReadOnlyList<SharedFile>> EveryFileAsync(BrokerClient broker, CancellationToken token)
    {
        var files = new List<SharedFile>();
        string? next = null;

        do
        {
            var listing = await broker.ListSharedFilesAsync(null, FileLimits.MaximumPageSize, next, token).ConfigureAwait(false);

            files.AddRange(listing.Page.Files);
            next = listing.Page.NextPageToken;
        }
        while (next is not null);

        return files;
    }

    private static string Describe(RemoveFilesOutcome outcome)
    {
        if (outcome.Rejection == FileRejection.TooManyNames)
        {
            return $"At most {FileLimits.NamesPerRemoval} files can be removed at once. Nothing was removed.";
        }

        var said = string.Create(
            CultureInfo.InvariantCulture,
            $"Removed {outcome.Removed.Count} file(s), freeing {Size(outcome.BytesFreed)}.");

        foreach (var refused in outcome.Refused)
        {
            said += $" {refused.File.Name}: {refused.Detail ?? refused.Rejection.ToString()}";
        }

        return said;
    }
}
