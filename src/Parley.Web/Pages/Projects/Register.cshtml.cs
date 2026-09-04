using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// How to register a new agent with this project.
/// </summary>
/// <remarks>
/// Instructions rather than a form, and deliberately. An agent's identity comes from the launch
/// configuration of the process that will act as it (SPEC.md §13), so a console that registered
/// an agent would be naming one on behalf of a process that had not started — which is the
/// forgery the whole trust model rests on preventing.
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class RegisterModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project the new agent would join.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The endpoint that project's broker listens on.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>Reads the project's endpoint.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public IActionResult OnGet()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        Endpoint = project.Endpoint;

        return Page();
    }
}
