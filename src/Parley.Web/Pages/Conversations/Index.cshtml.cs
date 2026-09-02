using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;

namespace Parley.Web.Pages.Conversations;

/// <summary>
/// Where the conversation list used to live.
/// </summary>
/// <remarks>
/// <para>
/// It is on the project's own page now, beside the roster, because the two are read together: an
/// operator opening a project is asking "who is here and what is happening", and answering that
/// across two pages made them hold one half in their head while looking at the other.
/// </para>
/// <para>
/// The route stays rather than being deleted. Links to it exist — in notes, in a browser's
/// history, in whatever somebody pasted to a colleague — and a dead one teaches a reader that the
/// console loses things. It carries the narrowing across, so a link to one agent's conversations
/// still arrives at one agent's conversations.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class IndexModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project was being asked for.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The participant filter to carry across, if there was one.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Participant { get; set; }

    /// <summary>The resume point to carry across, if there was one.</summary>
    [BindProperty(SupportsGet = true)]
    public string? PageToken { get; set; }

    /// <summary>Sends the reader to the project this list belonged to.</summary>
    /// <returns>A redirect, always.</returns>
    public IActionResult OnGet() =>
        directory.Find(Project) is null

            // A project this machine does not have is the wrong address, not an error page — the
            // same answer every other project-scoped route gives.
            ? RedirectToPage("/Index")
            : RedirectToPage(
                "/Projects/Agents",
                new { project = Project, participant = Participant, pageToken = PageToken });
}
