using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// Everything the registry holds about one agent, and what may be done to it.
/// </summary>
/// <remarks>
/// <para>
/// Split out of the roster because the two are read for different reasons. A roster answers "who
/// is here"; this answers "what is that one", and the fields that matter to the second question —
/// the workspace, the adapter, what it says it owns — are exactly the ones that make a list
/// unreadable when they are in it.
/// </para>
/// <para>
/// The workspace is here and never on an agent-facing surface. §10.1's <c>agents()</c> withholds
/// it deliberately; this is the operator's view, and the difference between the two is the whole
/// reason the operations are named apart.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class AgentModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which agent.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Agent { get; set; }

    /// <summary>What the registry says about it, or null when it holds no such agent.</summary>
    public AgentRecord? Record { get; private set; }

    /// <summary>Why this project could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>When the broker answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>Reads one agent out of the registry.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    /// <remarks>
    /// Filtered here rather than asked for by name, because §10.2 has no operation that reads one
    /// agent — <c>registry</c> returns the roster whole. A page that invented a narrower call
    /// would be a client asking for something no broker promises.
    /// </remarks>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Agent))
        {
            return RedirectToPage("/Index");
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .RegistryAsync(HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        Record = (read.Value ?? [])
            .FirstOrDefault(a => string.Equals(a.Name.Value, Agent, StringComparison.Ordinal));

        Problem = read.Problem;
        AsOf = DateTimeOffset.Now;

        return Page();
    }
}
