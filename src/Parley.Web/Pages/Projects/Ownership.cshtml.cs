using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Core;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// The ownership review: every agent, the line its peers read, where that line came from, and whatever Parley is waiting
/// on (SPEC.md §10.2, FR-016 to FR-018).
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes Auto Accept safe to leave on</b> — the owner's condition for it. A line accepted automatically is one no
/// person read, and this page is where somebody reads it and corrects it. The provenance is in words from
/// <see cref="Provenance"/>, which the terminal's review reads too, so the two surfaces cannot describe one record two
/// ways.
/// </para>
/// <para>
/// <b>It only reads.</b> Every decision is a link to <c>Act</c>, which says what it would do on a GET before a POST does
/// it — the rule <c>EveryActionHasADoorTests</c> holds this console to.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class OwnershipModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The project's Auto Accept switch, once read.</summary>
    public bool AutoAccept { get; private set; }

    /// <summary>Every agent the review lists, beside its latest question from Parley.</summary>
    public IReadOnlyList<(AgentRecord Agent, OwnershipQuestion? Question)> Rows { get; private set; } = [];

    /// <summary>Why the broker could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>Reads the registry and Parley's questions.</summary>
    /// <returns>The page.</returns>
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
            (await broker.RegistryAsync(token).ConfigureAwait(false),
             await broker.FacilitationAsync(token).ConfigureAwait(false))).ConfigureAwait(false);

        if (!read.Reachable)
        {
            Problem = read.Problem;

            return Page();
        }

        var (registry, report) = read.Value;

        AutoAccept = report.AutoAccept;

        // Every active agent that is neither a seat nor Parley: a seat is a person, and Parley asks rather than owning
        // anything.
        Rows = [.. registry
            .Where(r => r.Status == AgentStatus.Active && !r.Facilitator)
            .Where(r => !string.Equals(r.Adapter, Seats.Adapter, StringComparison.Ordinal))
            .OrderBy(r => r.Name.Value, StringComparer.Ordinal)
            .Select(r => (r, report.Questions.FirstOrDefault(q => q.Agent == r.Name)))];

        return Page();
    }
}
