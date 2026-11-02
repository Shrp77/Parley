using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Core;

namespace Parley.Web.Pages.Roadmaps;

/// <summary>
/// Declining a proposed roadmap, having first said what that does (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The deliberate act is a page of its own.</b> A GET describes what declining does — nothing is kept, the proposer is
/// told, the conversation it came from is untouched — and only a POST declines. A button beside the review that declined
/// at a click would be a discard behind one keystroke, which this console does not offer for anything.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class DeclineModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which proposal.</summary>
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The proposal, once read.</summary>
    public RoadmapProposal? Proposal { get; private set; }

    /// <summary>Why the broker could not be read, or why it was refused.</summary>
    public string? Problem { get; private set; }

    /// <summary>What happened, once it has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Says what declining would do. Nothing is declined here.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is null)
        {
            return RedirectToPage("/Index");
        }

        await ReadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Declines the proposal.</summary>
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

        var outcome = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .DeclineProposalAsync(Proposal.Id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable || !outcome.Value!.Done)
        {
            Problem = outcome.Reachable ? outcome.Value!.Problem : outcome.Problem;

            return Page();
        }

        Outcome = $"Declined '{Proposal.Content.Title}'. {Proposal.Proposer.Role} was told.";

        return Page();
    }

    private async Task ReadAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return;
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ProposalAsync(Id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!read.Reachable)
        {
            Problem = read.Problem;

            return;
        }

        Proposal = read.Value is { State: ProposalState.Waiting } waiting ? waiting : null;

        if (Proposal is null)
        {
            Problem = "That proposal is not awaiting your decision. It may have been decided already.";
        }
    }
}
