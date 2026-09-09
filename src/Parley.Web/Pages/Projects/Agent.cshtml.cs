using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;

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

    /// <summary>What this operator told this agent lately, and what became of each.</summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than on a page of its own, because it is a fact about this agent.</b> A
    /// second page would be a second place to look for it, and somebody who has just instructed an
    /// agent is already looking at this one.
    /// </para>
    /// <para>
    /// <b>It is what makes the composer safe to make non-blocking.</b> Issuing an instruction used
    /// to hold the browser's request open for the whole turn, and the reply existed nowhere else —
    /// so a page that returned immediately without this would have made the answer less reachable
    /// rather than more.
    /// </para>
    /// </remarks>
    public IReadOnlyList<InstructionRecord> Instructions { get; private set; } = [];

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

        // Only for an agent the registry actually holds, and only when the broker answered. A
        // listing for a name that is not registered would be empty for two different reasons —
        // nothing was ever said to it, and there is no such agent — which a reader cannot tell
        // apart from an empty list.
        if (Record is { } known)
        {
            var told = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .InstructionsAsync(known.Name, cancellationToken: HttpContext.RequestAborted)
                    .ConfigureAwait(false))
                .ConfigureAwait(false);

            Instructions = told.Value?.Instructions ?? [];

            // Not overwritten if the registry read already failed: the first problem is the one
            // that explains the page, and the second is a consequence of it.
            Problem ??= told.Problem;
        }

        return Page();
    }
}
