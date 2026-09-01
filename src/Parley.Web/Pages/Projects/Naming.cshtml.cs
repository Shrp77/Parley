using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// How to rename a project on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Instructions rather than a form, and for the same reason as agent registration: the manifest
/// of SPEC.md §4.1 is read by two programs and written by exactly one of them. The console reads
/// it; <c>Parley.Cli</c> writes it. A rename button here would make this a second writer of a
/// file whose whole safety comes from having one, and the console does not even reference
/// anything that could open it.
/// </para>
/// <para>
/// The naming is a fact about this machine and nothing else. §4.1 gives the protocol no concept
/// of a project at all — membership is which endpoint an agent connected to — so renaming
/// changes what this machine calls a deployment and reaches nothing on the wire. No agent
/// notices, no history moves, and nothing needs restarting.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class NamingModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Where that project's broker listens.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>The store that broker owns, when this machine's manifest names one.</summary>
    public string Store { get; private set; } = string.Empty;

    /// <summary>Whether this project is named in a manifest at all.</summary>
    /// <remarks>
    /// A machine running by explicit paths has no manifest, and the console offers the broker
    /// its sibling commands use under a stand-in name. There is nothing to rename in that case —
    /// what is wanted is the first naming, which is a different command and a different sentence.
    /// </remarks>
    public bool Named { get; private set; }

    /// <summary>Reads what this machine knows about the project.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public IActionResult OnGet()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        Endpoint = project.Endpoint;
        Store = project.Store;
        Named = project.Store.Length > 0;

        return Page();
    }
}
