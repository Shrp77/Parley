using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Roadmaps;
using Parley.Core;

namespace Parley.Web.Pages.Roadmaps;

/// <summary>
/// Changing a confirmed roadmap: removing, rewording and adding items, as text (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>A GET describes and a POST saves.</b> The roadmap is written out in the same text the proposal page edits, with each
/// existing line carrying <c>item: n</c> so that an edit that rewords or reorders it still means the same item. A line with
/// none is a new item, and a line taken out is a removed one.
/// </para>
/// <para>
/// <b>The revision the form was opened at travels with it.</b> The broker refuses an edit made on any other revision, so
/// two surfaces editing at once cannot overwrite each other. This page also reads the roadmap again before it sends, so a
/// change made meanwhile is reported with the roadmap as it now stands and the person's own text is not lost.
/// </para>
/// <para>
/// <b>Nothing here tells an agent anything.</b> No agent operation reads or changes a roadmap; work reaches an agent only as
/// a request the person sends.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class EditModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which roadmap.</summary>
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The roadmap as text, which the person edits.</summary>
    [BindProperty, FromForm]
    public string? Text { get; set; }

    /// <summary>The revision the form was opened at.</summary>
    [BindProperty, FromForm]
    public int Revision { get; set; }

    /// <summary>Set by the second submit, after the page described the open work the edit removes.</summary>
    [BindProperty, FromForm]
    public bool ConfirmRemoval { get; set; }

    /// <summary>The numbers of the items the warning described, which the second submit agrees to remove.</summary>
    [BindProperty, FromForm]
    public string? Removing { get; set; }

    /// <summary>What the edit would leave in flight, said before it is saved; null when nothing is.</summary>
    public string? Warning { get; private set; }

    /// <summary>The roadmap, once read.</summary>
    public Roadmap? Roadmap { get; private set; }

    /// <summary>The roadmap's text as it now stands, shown when it changed since the form was opened.</summary>
    public string? CurrentText { get; private set; }

    /// <summary>What is wrong with what was sent, or why the broker could not be read.</summary>
    public string? Problem { get; private set; }

    /// <summary>What happened, once it has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Describes the roadmap as text and offers it for editing. Nothing is changed here.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is null)
        {
            return RedirectToPage("/Index");
        }

        await ReadAsync().ConfigureAwait(false);

        if (Roadmap is not null)
        {
            Text = TextOf(Roadmap);
            Revision = Roadmap.Revision;
        }

        return Page();
    }

    /// <summary>Saves the text the person sent as the roadmap's new content.</summary>
    /// <returns>The page, saying what became of it.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        await ReadAsync().ConfigureAwait(false);

        if (Roadmap is null)
        {
            return Page();
        }

        var (content, problem) = RoadmapTextCodec.Parse(Text);

        if (content is null || problem is not null)
        {
            Problem = problem ?? "That is not a roadmap.";

            return Page();
        }

        if (Roadmap.Revision != Revision)
        {
            Problem = "The roadmap changed after you opened it. Your text is kept; the roadmap as it is now is below.";
            CurrentText = TextOf(Roadmap);
            Revision = Roadmap.Revision;

            return Page();
        }

        // Removing an item whose work is still open is described first and saved only on a second, deliberate submit:
        // the request stays open and its agent is not told, which deleting a line does not say (SPEC.md §5.7). Going ahead
        // covers the items that were described and no others, so an open item taken out after the warning is described too.
        var open = Removal.StillOpen(Roadmap, content);

        if (!Removal.Covers(open, ConfirmRemoval ? Removing : null))
        {
            Warning = Removal.Describe(open);
            Removing = Removal.Numbers(open);

            return Page();
        }

        var outcome = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .EditRoadmapAsync(Roadmap.Id, Revision, content, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            Problem = outcome.Problem;

            return Page();
        }

        if (!outcome.Value!.Done)
        {
            Problem = outcome.Value.Problem ?? "It could not be saved.";

            return Page();
        }

        Outcome = $"Saved '{content.Title}'.";

        return Page();
    }

    /// <summary>Writes a roadmap's live items as text, each keeping its number.</summary>
    private static string TextOf(Roadmap roadmap) =>
        RoadmapTextCodec.Write(RoadmapDraft.From(roadmap).ToContent());

    /// <summary>Reads the roadmap.</summary>
    private async Task ReadAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return;
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var read = await BrokerDirectory.ReadAsync(async () =>
            await broker.RoadmapAsync(Id, token).ConfigureAwait(false)).ConfigureAwait(false);

        if (!read.Reachable)
        {
            Problem = read.Problem;

            return;
        }

        if (read.Value is not { } roadmap)
        {
            Problem = "There is no such roadmap in this project.";

            return;
        }

        Roadmap = roadmap;
    }
}
