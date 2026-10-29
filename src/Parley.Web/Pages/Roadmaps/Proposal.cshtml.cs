using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Roadmaps;
using Parley.Core;

namespace Parley.Web.Pages.Roadmaps;

/// <summary>
/// One proposed roadmap: what it says, and the person's choice to confirm it, as it is or changed (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>A GET describes and a POST confirms.</b> The proposal is shown whole, in the agent's words and escaped, with the
/// plain statement of what it is not: a roadmap, or an assignment. The box beside it holds the proposal as text, so
/// confirming it unchanged and confirming an edit are the same button.
/// </para>
/// <para>
/// <b>Whether it was edited is the broker's comparison and never this form's.</b> The text is parsed here only to tell the
/// person what is wrong before anything is sent; what reaches the broker is content, and the broker decides whether it is
/// the proposal's own words.
/// </para>
/// <para>
/// <b>The title hint is shown, never acted on</b>: when it matches an existing roadmap the person chooses
/// between a revision and a new roadmap, and the default is the one that leaves the old roadmap alone.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class ProposalModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which proposal.</summary>
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The proposal as text, which the person edits.</summary>
    [BindProperty, FromForm]
    public string? Text { get; set; }

    /// <summary>The roadmap to revise, or <c>new</c> for a roadmap of its own.</summary>
    [BindProperty, FromForm]
    public string? Target { get; set; }

    /// <summary>The revision of the roadmap to revise that the page showed, so a revision never replaces items saved since.</summary>
    [BindProperty, FromForm]
    public int? Revision { get; set; }

    /// <summary>Set by the second submit, after the page described the open work a revision removes.</summary>
    [BindProperty, FromForm]
    public bool ConfirmRemoval { get; set; }

    /// <summary>The numbers of the items the warning described, which the second submit agrees to remove.</summary>
    [BindProperty, FromForm]
    public string? Removing { get; set; }

    /// <summary>What confirming as a revision would leave in flight, said before it is confirmed; null when nothing is.</summary>
    public string? Warning { get; private set; }

    /// <summary>The proposal being decided, once read.</summary>
    public RoadmapProposal? Proposal { get; private set; }

    /// <summary>The roadmap its title hint matches, when one does.</summary>
    public RoadmapSummary? Match { get; private set; }

    /// <summary>What is wrong with what was sent, or why the broker could not be read.</summary>
    public string? Problem { get; private set; }

    /// <summary>What happened, once it has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Describes the proposal and offers it for confirmation. Nothing is decided here.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is null)
        {
            return RedirectToPage("/Index");
        }

        await ReadAsync().ConfigureAwait(false);

        if (Proposal is not null)
        {
            Text = RoadmapTextCodec.Write(Proposal.Content);
        }

        return Page();
    }

    /// <summary>Confirms the proposal with the content the person sent.</summary>
    /// <returns>The page, saying what became of it.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        await ReadAsync().ConfigureAwait(false);

        if (Proposal is null)
        {
            return Page();
        }

        var (content, problem) = RoadmapTextCodec.Parse(Text);

        if (content is null || problem is not null)
        {
            Problem = problem ?? "That is not a roadmap.";

            return Page();
        }

        long? target = Match is not null && string.Equals(Target, Match.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? Match.Id
            : null;

        // A revision the person chose is never confirmed as something else. When the roadmap it named no longer matches,
        // renamed meanwhile say, nothing is confirmed and the page is shown again as it now reads.
        if (target is null && Target is { Length: > 0 } chosen && !string.Equals(chosen, "new", StringComparison.Ordinal))
        {
            Problem = "The roadmap you chose to revise is no longer the one this proposal matches, so nothing was confirmed. "
                + "Choose again.";
            Target = null;

            return Page();
        }

        // A proposal's items carry no item numbers, so a revision replaces every item the roadmap had. Open work it would
        // remove is described first and confirmed only on a second, deliberate submit, as an edit's is (SPEC.md §5.7).
        if (target is { } revising)
        {
            var current = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .RoadmapAsync(revising, HttpContext.RequestAborted).ConfigureAwait(false))
                .ConfigureAwait(false);

            if (!current.Reachable || current.Value is null)
            {
                Problem = current.Problem ?? "The roadmap it would revise could not be read, so nothing was confirmed.";

                return Page();
            }

            var open = Removal.StillOpen(current.Value, content);

            if (!Removal.Covers(open, ConfirmRemoval ? Removing : null))
            {
                Warning = Removal.Describe(open);
                Removing = Removal.Numbers(open);

                return Page();
            }
        }

        var outcome = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ConfirmProposalAsync(
                    Proposal.Id, content, target, target is null ? null : Revision, HttpContext.RequestAborted)
                .ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            Problem = outcome.Problem;

            return Page();
        }

        if (!outcome.Value!.Done)
        {
            Problem = outcome.Value.Problem ?? "It could not be confirmed.";

            return Page();
        }

        Outcome = $"Confirmed '{content.Title}'. {Proposal.Proposer.Role} was told.";

        return Page();
    }

    /// <summary>Reads the proposal and the roadmap its title hint matches.</summary>
    private async Task ReadAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return;
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var read = await BrokerDirectory.ReadAsync(async () =>
            (await broker.ProposalAsync(Id, token).ConfigureAwait(false),
             await broker.RoadmapsAsync(token).ConfigureAwait(false))).ConfigureAwait(false);

        if (!read.Reachable)
        {
            Problem = read.Problem;

            return;
        }

        var (proposal, roadmaps) = read.Value;

        Proposal = proposal is { State: ProposalState.Waiting } ? proposal : null;

        if (Proposal is null)
        {
            Problem = "That proposal is not awaiting your decision. It may have been decided already.";

            return;
        }

        Match = ProposalText.Matching(Proposal, roadmaps);
    }
}
