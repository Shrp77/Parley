using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;

namespace Parley.Web.Pages.Conversations;

/// <summary>Moving whole exchanges to another conversation (SPEC.md §5.0, <c>move_exchanges</c>).</summary>
/// <remarks>
/// <para>
/// <b>What is done on the POST is described on the GET.</b> The page lists every exchange that goes, says which were added
/// and why and which are still open, and names the files that stay. The deliberate act is naming or choosing where they go
/// in that same step and pressing the button. A link can pick what is listed, and cannot move anything.
/// </para>
/// <para>
/// <b>The POST carries what the page listed</b>, so what moves is what was shown. The broker refuses a list that leaves part
/// of a group behind rather than adding to it.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="seats">The seats this console holds.</param>
/// <param name="notices">What the console tells the person.</param>
public sealed class MoveModel(BrokerDirectory directory, SeatBook seats, Parley.Web.Notifications.NotificationLog notices) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The conversation they are in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    /// <summary>The exchanges picked, from the address.</summary>
    [BindProperty(SupportsGet = true, Name = "x")]
    public IList<long> Picked { get; set; } = [];

    /// <summary>Every exchange that goes, as the page listed them.</summary>
    [BindProperty, FromForm]
    public IList<long> Exchanges { get; set; } = [];

    /// <summary>Whether they go to a new conversation or an existing one.</summary>
    [BindProperty, FromForm]
    public string? Where { get; set; }

    /// <summary>The new conversation's name.</summary>
    [BindProperty, FromForm]
    public string? NewName { get; set; }

    /// <summary>The existing conversation chosen.</summary>
    [BindProperty, FromForm]
    public string? Existing { get; set; }

    /// <summary>What would go, or every exchange to choose from when nothing is picked yet.</summary>
    public MovePreview? Preview { get; private set; }

    /// <summary>Whether this is the list to pick from rather than the confirmation.</summary>
    public bool Picking { get; private set; }

    /// <summary>Open conversations they could go to.</summary>
    public IReadOnlyList<ConversationSummary> Destinations { get; private set; } = [];

    /// <summary>Why this could not be drawn or done.</summary>
    public string? Problem { get; private set; }

    /// <summary>Draws what would move. Changes nothing.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is not { } project || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        await DrawAsync(project, Picked).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Moves what the page listed.</summary>
    /// <returns>The destination, or this page saying why nothing moved.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        if (directory.Find(Project) is not { } project || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        var token = HttpContext.RequestAborted;
        var isNew = !string.Equals(Where, "existing", StringComparison.Ordinal);
        var destination = (isNew ? NewName : Existing)?.Trim();

        if (string.IsNullOrEmpty(destination))
        {
            Problem = isNew ? "It needs a name, so nothing moved." : "No conversation was chosen, so nothing moved.";
            await DrawAsync(project, Exchanges).ConfigureAwait(false);

            return Page();
        }

        var moved = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .MoveExchangesAsync(Id, destination, isNew, [.. Exchanges], token).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (moved.Value is not { Moved: true } done)
        {
            Problem = moved.Reachable ? $"Nothing moved: {moved.Value?.Refused}" : moved.Problem;
            await DrawAsync(project, Exchanges).ConfigureAwait(false);

            return Page();
        }

        // What this console held of them names the old conversation. The next read gets the new one.
        foreach (var exchange in Exchanges)
        {
            seats.Held(project.Name.Value)?.Forget(exchange);
        }

        notices.Add(
            project.Name.Value,
            destination,
            "Move",
            $"Moved {done.Exchanges} exchange{(done.Exchanges == 1 ? string.Empty : "s")} from {Id}.",
            true);

        return RedirectToPage("/Conversations/Detail", new { project = project.Name.Value, id = destination });
    }

    private async Task DrawAsync(ProjectSettings project, IList<long> picked)
    {
        var token = HttpContext.RequestAborted;
        var client = new BrokerClient(directory.ClientFor(project));
        var chosen = picked;

        Picking = picked.Count == 0;

        if (Picking)
        {
            var whole = await BrokerDirectory.ReadAsync(async () =>
                await client.ExportAsync(Id!, token).ConfigureAwait(false)).ConfigureAwait(false);

            if (whole.Value is not { } export)
            {
                Problem ??= whole.Problem;
                return;
            }

            chosen = [.. export.Exchanges.Select(e => e.ExchangeId)];

            if (chosen.Count == 0)
            {
                Problem ??= "There is nothing in this conversation to move.";
                return;
            }
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await client.PreviewMoveAsync(Id!, [.. chosen], token).ConfigureAwait(false)).ConfigureAwait(false);

        if (read.Value is not { } preview)
        {
            Problem ??= read.Problem;
            return;
        }

        Preview = preview;
        Problem ??= preview.Refused;

        if (Picking || preview.Refused is not null)
        {
            return;
        }

        var page = await BrokerDirectory.ReadAsync(async () =>
            await client.ConversationsAsync(cancellationToken: token).ConfigureAwait(false)).ConfigureAwait(false);

        Destinations =
        [
            .. (page.Value?.Items ?? [])
                .Where(c => !c.Closed && c.Item is null && !string.Equals(c.Id, Id, StringComparison.Ordinal)
                    && !c.Id.StartsWith("owns-", StringComparison.Ordinal)),
        ];
    }
}
