using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Files;
using Parley.Core;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// A project's file location: what is in it, and the person's four operations on it (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Removing and pruning each describe what will go before anything goes.</b> Removal lists the files, their sizes
/// and who placed them, and takes a second press; pruning says how many files and how much space, and takes the
/// project's name typed back — the door this console already puts in front of destroying a conversation, because
/// both delete somebody else's work for good.
/// </para>
/// <para>
/// <b>Opening launches on the machine running this console, and only there.</b> The broker says which machine holds
/// the location; a console viewed from another machine's browser opens files where the console runs, or says where
/// they are when that is not the broker's machine (the owner's decision). Every action is a POST, so a link or an
/// image elsewhere cannot open, remove or prune anything, and Razor Pages refuses a POST without this page's
/// antiforgery token.
/// </para>
/// <para>
/// Names are other participants' text and are HTML-encoded by Razor like everything else a page renders.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class FilesModel(BrokerDirectory directory) : PageModel
{
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

    /// <summary>Describes a removal, or makes it once confirmed.</summary>
    /// <param name="selected">The files chosen, each as its conversation and name separated by '/'.</param>
    /// <param name="confirmed">"yes" once the description has been shown and the person pressed again.</param>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnPostRemoveAsync(string[]? selected, string? confirmed)
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
    public async Task<IActionResult> OnPostPruneAsync(string? typed)
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

    /// <summary>Opens one file in its registered editor, on this machine.</summary>
    /// <param name="file">The file's name.</param>
    /// <param name="inConversation">Its conversation.</param>
    /// <returns>The page, saying what happened.</returns>
    public async Task<IActionResult> OnPostOpenAsync(string? file, string? inConversation)
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
