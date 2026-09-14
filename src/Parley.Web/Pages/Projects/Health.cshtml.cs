using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// What a deployment is doing to itself: work that has stalled, refusals, clarification depth, what
/// Results reported, and who is waiting on whom.
/// </summary>
/// <remarks>
/// <para>
/// These are the reads SPEC.md §10.2 keeps so that the protocol's provisional numbers can be argued about
/// from evidence. The message cap is 8,000 because these reads said so, and §9.3 keeps the count precisely
/// so the number is not moved on the strength of whoever complained most recently.
/// </para>
/// <para>
/// <b>Flags come first.</b> Nothing in the protocol times out, so work nobody picked up, or picked up and
/// left, stays open until a person sees it — and this page is where they do.
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

    /// <summary>What Results reported: outcomes, blockers, sources and closures.</summary>
    public ResultsReport? Results { get; private set; }

    /// <summary>Open work that needs a person to look at it, and the cycles recorded.</summary>
    public FlagReport? Flags { get; private set; }

    /// <summary>Who is waiting on whom.</summary>
    public IReadOnlyList<WaitingEdge> Waiting { get; private set; } = [];

    /// <summary>Why this project could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>When the broker answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>A flag in words.</summary>
    /// <param name="flag">The flag.</param>
    /// <returns>A sentence naming who is waiting, on whom, and since when.</returns>
    public static string Describe(Flag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);

        var since = flag.SinceUtc.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture);

        return flag.Kind switch
        {
            FlagKind.NotCollected =>
                $"{flag.WaitingOn.Value} has not collected what {flag.Waiting.Value} sent, since {since}.",
            FlagKind.NoProgress =>
                $"{flag.WaitingOn.Value} collected {flag.Waiting.Value}'s request and has done nothing on it since {since}.",
            FlagKind.NotVoted =>
                $"The work in {flag.ConversationId} is all closed and {flag.Waiting.Value} is finished; closing it has waited on {flag.WaitingOn.Value} to say whether it is too since {since}.",
            _ =>
                $"{flag.WaitingOn.Value} is retired and still holds {flag.Waiting.Value}'s request, since {since}.",
        };
    }

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
            Flags = await broker.FlagsAsync(token).ConfigureAwait(false);
            Refusals = await broker.RefusalsAsync(token).ConfigureAwait(false);
            Clarifications = await broker.ClarificationsAsync(token).ConfigureAwait(false);
            Results = await broker.ResultsAsync(token).ConfigureAwait(false);

            return await broker.WaitingAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        Waiting = read.Value ?? [];
        Problem = read.Problem;
        AsOf = DateTimeOffset.Now;

        return Page();
    }
}
