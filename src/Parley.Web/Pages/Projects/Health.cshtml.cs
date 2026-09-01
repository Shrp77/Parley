using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// What a deployment is doing to itself: refusals, clarification depth, referrals, notification
/// volume, and who is waiting on whom.
/// </summary>
/// <remarks>
/// <para>
/// These are the reads SPEC.md §10.2 keeps so that the protocol's provisional numbers can be
/// argued about from evidence. The message cap is 8,000 because these reads said so — forty-three
/// refusals across six agents, none of them near twice the old limit — and §9.3 keeps the count
/// precisely so the number is not moved on the strength of whoever complained most recently.
/// It moved once on this evidence and stays provisional on the same terms.
/// </para>
/// <para>
/// The wait-for graph is here rather than beside the conversations because it is a fact about the
/// deployment rather than about any one discussion: it is how an operator sees a question nobody
/// is going to answer.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class HealthModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>What the size cap turned away.</summary>
    public IReadOnlyList<SizeRefusal> Refusals { get; private set; } = [];

    /// <summary>What clarification cost.</summary>
    public ClarifyReport? Clarifications { get; private set; }

    /// <summary>Where work was sent when it was out of scope.</summary>
    public ReferralReport? Referrals { get; private set; }

    /// <summary>What Notify has carried.</summary>
    public NotifyVolumeReport? Notifications { get; private set; }

    /// <summary>Who is waiting on whom.</summary>
    public IReadOnlyList<WaitingEdge> Waiting { get; private set; } = [];

    /// <summary>Why this project could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>When the broker answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>Reads every health report for one project.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var read = await BrokerDirectory.ReadAsync(async () =>
        {
            Refusals = await broker.RefusalsAsync(token).ConfigureAwait(false);
            Clarifications = await broker.ClarificationsAsync(token).ConfigureAwait(false);
            Referrals = await broker.ReferralsAsync(token).ConfigureAwait(false);
            Notifications = await broker.NotificationsAsync(token).ConfigureAwait(false);

            return await broker.WaitingAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        Waiting = read.Value ?? [];
        Problem = read.Problem;
        AsOf = DateTimeOffset.Now;

        return Page();
    }
}
