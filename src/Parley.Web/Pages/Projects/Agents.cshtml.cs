using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;
using Parley.Client.Views;


namespace Parley.Web.Pages.Projects;

/// <summary>
/// One project's conversations, which can be narrowed to the ones an agent is party to.
/// </summary>
/// <remarks>
/// The roster, roadmaps, health, files and settings each have their own page in the project's rail. The roster
/// is read here only to offer the filter. Two contained reads rather than one: a registry that does not answer must cost
/// the filter and leave the conversations, and the reverse.
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

    /// <summary>Whether archived conversations are in the list.</summary>
    /// <remarks>
    /// In the query string rather than in a cookie, so a link to this page carries what the sender
    /// was looking at. A view preference kept per browser would make two people describing the same
    /// screen disagree about what is on it.
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public bool ShowArchived { get; set; }

    /// <summary>How the conversations are ordered.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    /// <summary>The agents registered in it.</summary>
    public IReadOnlyList<AgentRecord> Agents { get; private set; } = [];

    /// <summary>What they have been talking about, most recent first.</summary>
    public IReadOnlyList<ConversationSummary> Conversations { get; private set; } = [];

    /// <summary>The token for the following page, if there is one.</summary>
    public string? NextPageToken { get; private set; }

    /// <summary>What the list says about the archived conversations it is not showing.</summary>
    /// <remarks>
    /// Always rendered when anything is hidden. Archiving does not stop a conversation receiving
    /// messages and a message does not un-archive it, so without this a busy conversation somebody
    /// filed away last month is invisible with nothing saying so.
    /// </remarks>
    public string Hidden { get; private set; } = string.Empty;

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

    /// <summary>The agents the conversations can be narrowed to, as the filter offers them.</summary>
    /// <remarks>The roster, plus the agent being filtered on when it is not on it, so the filter never hides what it applies.</remarks>
    public IReadOnlyList<string> ParticipantChoices { get; private set; } = [];

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

        // Parley is not an agent, so it is not on the roster (SPEC.md principle 2.11): a row for it would link to a page
        // offering retire and purge, each of which the broker refuses. It is shown where it acts — as the sender
        // of its questions, and on the ownership review.
        Agents = [.. (roster.Value ?? []).Where(agent => !agent.Facilitator)];
        Problem = roster.Problem;

        var choices = Agents.Select(agent => agent.Name.Value).ToList();

        if (!string.IsNullOrWhiteSpace(Participant) && !choices.Contains(Participant, StringComparer.Ordinal))
        {
            choices.Add(Participant);
        }

        ParticipantChoices = choices;

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
                includeArchived: ShowArchived, cancellationToken: token).ConfigureAwait(false))
            .ConfigureAwait(false);

        var rows = ConversationsView.Sorted(
            ConversationsView.Rows(listed.Value?.Items ?? []),
            string.Equals(Sort, "name", StringComparison.Ordinal) ? SortBy.Name : SortBy.Activity);

        // Ordered through the shared derivation and then mapped back, so the console and the
        // terminal cannot sort the same list differently: anything both surfaces decide is decided once.
        Conversations =
        [
            .. rows.Select(row => (listed.Value?.Items ?? []).First(c => c.Id == row.Id))
        ];

        NextPageToken = listed.Value?.NextPageToken;
        Hidden = ConversationsView.Hidden(listed.Value?.ArchivedOmitted ?? 0, ShowArchived);
        ConversationProblem = listed.Problem;
        AsOf = DateTimeOffset.Now;

        return Page();
    }

    /// <summary>Files a conversation away, or brings it back.</summary>
    /// <param name="id">The conversation.</param>
    /// <param name="archived">Whether it is archived now, so this reverses it.</param>
    /// <returns>The page again, showing the result.</returns>
    /// <remarks>
    /// No typed name: nothing is destroyed and one click reverses it, so a confirmation here
    /// would be a dialog in front of a light switch.
    /// </remarks>
    public async Task<IActionResult> OnPostArchiveAsync([FromForm] string id, [FromForm] bool archived)
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(id))
        {
            return RedirectToPage("/Index");
        }

        var token = HttpContext.RequestAborted;
        var broker = new BrokerClient(directory.ClientFor(project));

        _ = await BrokerDirectory.ReadAsync(async () => archived
            ? await broker.UnarchiveAsync(id, token).ConfigureAwait(false)
            : await broker.ArchiveAsync(id, token).ConfigureAwait(false)).ConfigureAwait(false);

        return RedirectToPage(new { project = Project, showArchived = ShowArchived, sort = Sort });
    }
}
