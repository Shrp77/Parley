using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Core;

namespace Parley.Web.Pages.Roadmaps;

/// <summary>
/// A project's roadmaps, and the ones agents have proposed that are waiting for a person (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Both are on one page because they are read together.</b> What is waiting is what a person came to decide, and the
/// roadmaps are what that decision makes; a person who has just confirmed one should see it arrive in the list below.
/// </para>
/// <para>
/// <b>It only reads.</b> Every decision is a link to a page that describes what it would do on a GET before a POST does it,
/// the rule this console holds every action to. Nothing here needs a seat: deciding a proposal speaks as nobody (principle
/// 2.12).
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class IndexModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The proposals agents have put forward that nobody has decided.</summary>
    public IReadOnlyList<RoadmapProposal> Waiting { get; private set; } = [];

    /// <summary>The project's roadmaps.</summary>
    public IReadOnlyList<RoadmapSummary> Roadmaps { get; private set; } = [];

    /// <summary>Why the broker could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>Reads the proposals and the roadmaps.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var read = await BrokerDirectory.ReadAsync(async () =>
            (await broker.ProposalsAsync(ProposalState.Waiting, headings: true, token).ConfigureAwait(false),
             await broker.RoadmapsAsync(token).ConfigureAwait(false))).ConfigureAwait(false);

        if (!read.Reachable)
        {
            Problem = read.Problem;

            return Page();
        }

        (Waiting, Roadmaps) = read.Value;

        return Page();
    }
}
