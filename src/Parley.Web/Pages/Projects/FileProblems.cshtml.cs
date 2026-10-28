using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Core;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// What sweeps of a project's file location found and could not leave as it was, and dismissing each (SPEC.md §9.5,
/// §10.2 <c>list_file_problems</c>, <c>dismiss_file_problem</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The page says what dismissing does before anything is dismissed.</b> Every problem is listed with what happened,
/// its conversation and path, the hashes and where a quarantined file went, under a sentence saying a dismissal deletes
/// nothing on disk; the button beside each is the act. A POST, so a link or an image elsewhere cannot dismiss anything,
/// and Razor Pages refuses one without this page's antiforgery token.
/// </para>
/// <para>
/// <b>Every path is somebody else's text</b>: an entry another program put in the location was never checked. Razor
/// encodes it like everything else a page renders.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class FileProblemsModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Whether the dismissed problems are listed too.</summary>
    [BindProperty(SupportsGet = true)]
    public bool All { get; set; }

    /// <summary>What the broker said, once read.</summary>
    public FileProblemsReport? Report { get; private set; }

    /// <summary>What happened, once something has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Why the page could not be read, if it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>Shows the problems.</summary>
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

    /// <summary>Dismisses one problem.</summary>
    /// <param name="id">The problem's number.</param>
    /// <returns>The page, saying what came of it.</returns>
    /// <remarks>
    /// One press, because the page already said what it does and it deletes nothing: the problem can be read again among
    /// the dismissed ones.
    /// </remarks>
    public async Task<IActionResult> OnPostDismissAsync([FromForm] long? id)
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        if (id is not { } chosen || chosen <= 0)
        {
            Outcome = "No problem was chosen. Nothing was dismissed.";
            await LoadAsync(project).ConfigureAwait(false);
            return Page();
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var dismissed = await BrokerDirectory.ReadAsync(async () =>
            await broker.DismissFileProblemAsync(chosen, token).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = !dismissed.Reachable
            ? dismissed.Problem
            : dismissed.Value
                ? $"Dismissed file problem #{chosen}. Nothing on disk was changed."
                : $"File problem #{chosen} was not waiting any more. Nothing was changed.";

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    private async Task LoadAsync(ProjectSettings project)
    {
        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var read = await BrokerDirectory.ReadAsync(async () =>
            await broker.ListFileProblemsAsync(All, token).ConfigureAwait(false)).ConfigureAwait(false);

        Report = read.Value;
        Problem ??= read.Problem;
    }
}
