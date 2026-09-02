using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// One project: who is registered in it, and what they have been talking about.
/// </summary>
/// <remarks>
/// <para>
/// The two halves are on one page because they are read together. An operator opening a project
/// is asking some version of "who is here and what is happening", and answering that across two
/// pages made them hold the roster in their head while looking at the conversations.
/// </para>
/// <para>
/// Two contained reads rather than one. A registry that does not answer must cost the roster and
/// leave the conversations, and the reverse — they come from the same broker but they are
/// separate questions, and half a page is worth more than an error.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class AgentsModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project this page is showing.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Restrict the conversations to those this agent is party to.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Participant { get; set; }

    /// <summary>Where to resume, for the next page of conversations.</summary>
    [BindProperty(SupportsGet = true)]
    public string? PageToken { get; set; }

    /// <summary>The agents registered in it.</summary>
    public IReadOnlyList<AgentRecord> Agents { get; private set; } = [];

    /// <summary>What they have been talking about, most recent first.</summary>
    public IReadOnlyList<ConversationSummary> Conversations { get; private set; } = [];

    /// <summary>The token for the following page, if there is one.</summary>
    public string? NextPageToken { get; private set; }

    /// <summary>Why the roster could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>Why the conversations could not be read, when they could not.</summary>
    public string? ConversationProblem { get; private set; }

    /// <summary>Why the participant filter was not applied, when it was not.</summary>
    /// <remarks>
    /// Said rather than swallowed. A filter that is not a name cannot be applied, and the
    /// alternative to saying so is a page that quietly returns every conversation in the project
    /// — not an empty result somebody would question but a full one they would read as the
    /// answer to what they asked.
    /// </remarks>
    public string? FilterProblem { get; private set; }

    /// <summary>When the broker answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>Reads the roster and the conversations for the named project.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            // A project this machine does not have is not an error page, it is the wrong address.
            return RedirectToPage("/Index");
        }

        var token = HttpContext.RequestAborted;
        var broker = new BrokerClient(directory.ClientFor(project));

        var roster = await BrokerDirectory.ReadAsync(async () =>
            await broker.RegistryAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        Agents = roster.Value ?? [];
        Problem = roster.Problem;

        AgentName? participant = null;

        if (!string.IsNullOrWhiteSpace(Participant))
        {
            if (AgentName.TryCreate(Participant, out var named))
            {
                participant = named;
            }
            else
            {
                FilterProblem =
                    $"'{Participant}' is not a name an agent can have, so it was not used to "
                    + "narrow this list. Everything in the project is shown.";
            }
        }

        var listed = await BrokerDirectory.ReadAsync(async () =>
            await broker.ConversationsAsync(participant, pageToken: PageToken,
                cancellationToken: token).ConfigureAwait(false)).ConfigureAwait(false);

        Conversations = listed.Value?.Items ?? [];
        NextPageToken = listed.Value?.NextPageToken;
        ConversationProblem = listed.Problem;
        AsOf = DateTimeOffset.Now;

        return Page();
    }
}
