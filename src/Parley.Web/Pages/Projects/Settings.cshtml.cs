using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Core;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// How this browser treats a project: today, which agent a new conversation is addressed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A preference of this browser, not a fact about the deployment.</b> It is kept in the browser's storage beside the
/// conversation filter and the recipients a person ticked, and it is never sent anywhere. A setting held by the broker
/// would be one more thing every surface owes, with a protocol decision behind it; this one changes only whom this
/// page ticks first, and the person still sees the box ticked and can untick it before sending.
/// </para>
/// <para>
/// The page only reads the registry, to offer the agents there are. Choosing one is script, because the choice has
/// nowhere on the server to go.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class SettingsModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The agents a new conversation could be addressed to.</summary>
    public IReadOnlyList<AgentRecord> Agents { get; private set; } = [];

    /// <summary>Why the agents could not be read, when they could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>Reads the agents of the project.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        var roster = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .RegistryAsync(HttpContext.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);

        // Not the person and not Parley: a conversation is begun by the person, to an agent.
        Agents =
        [
            .. (roster.Value ?? [])
                .Where(agent => !agent.Facilitator && agent.Name.Role != "human-user")
                .OrderBy(agent => agent.Name.Machine, StringComparer.Ordinal)
                .ThenBy(agent => agent.Name.Role, StringComparer.Ordinal)
        ];
        Problem = roster.Problem;

        return Page();
    }
}
