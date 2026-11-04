using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Rendering;
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
/// <param name="notices">What the console tells the person.</param>
public sealed class MoveModel(BrokerDirectory directory, Parley.Web.Notifications.NotificationLog notices) : PageModel
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

    /// <summary>Why an exchange goes though it was not picked: one send to several, which the broker keeps no record of.</summary>
    /// <param name="exchange">The exchange.</param>
    /// <returns>The words, or null when it was picked or the broker said why.</returns>
    public string? AddedBecause(MovePreviewExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        return exchange.AddedBecause ?? (Asked.Contains(exchange.Id) ? null : MovePicking.SentTogether);
    }

    private HashSet<long> Asked { get; set; } = [];

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

        // The radio doesn't follow the list by itself, so a person who only chose from the list meant the list, and one who
        // both named and chose hasn't said which. Nothing moves on a guess.
        if (isNew && !string.IsNullOrWhiteSpace(Existing))
        {
            if (!string.IsNullOrWhiteSpace(NewName))
            {
                Problem = "A new name and an existing conversation were both given, so nothing moved. Clear the one you don't mean.";
                await DrawAsync(project, Exchanges).ConfigureAwait(false);

                return Page();
            }

            isNew = false;
        }

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

        Picking = picked.Count == 0;
        Asked = [.. picked];

        var whole = await BrokerDirectory.ReadAsync(async () =>
            await client.ExportAsync(Id!, token).ConfigureAwait(false)).ConfigureAwait(false);

        if (whole.Value is not { } export)
        {
            Problem ??= whole.Problem;
            return;
        }

        // On the list to pick from every exchange is its own row. What goes is every part of what was picked.
        IList<long> chosen = Picking
            ? [.. export.Exchanges.Select(e => e.ExchangeId)]
            : [.. MovePicking.WithSendsToSeveral(export.Messages, [.. picked])];

        if (chosen.Count == 0)
        {
            Problem ??= "There is nothing in this conversation to move.";
            return;
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

        var destinations = await BrokerDirectory.ReadAsync(async () =>
            await MovePicking.DestinationsAsync(client, Id!, token).ConfigureAwait(false)).ConfigureAwait(false);

        Destinations = destinations.Value ?? [];
    }
}
