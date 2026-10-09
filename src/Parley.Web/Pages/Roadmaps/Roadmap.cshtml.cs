using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Roadmaps;
using Parley.Core;

namespace Parley.Web.Pages.Roadmaps;

/// <summary>
/// One confirmed roadmap: its items in order, where each stands, and a way into each one's conversation (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where an item stands is read, never set.</b> The words come from <see cref="ItemWording"/> over
/// <see cref="ItemStates"/>, the derivation the terminal reads too, so the two surfaces cannot disagree about whether an
/// item is waiting on the person or done.
/// </para>
/// <para>
/// <b>Starting an item is a link, not an operation.</b> An item not yet started links to its conversation with the item's
/// text in the box and every agent the roadmap names ticked. Nothing is sent by following it; sending is the person's act
/// on the conversation page, so the request is theirs and no endpoint here speaks for them.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class RoadmapModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which roadmap.</summary>
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The roadmap, once read.</summary>
    public Roadmap? Roadmap { get; private set; }

    /// <summary>Where each of its items stands, once read.</summary>
    public RoadmapStanding? Standing { get; private set; }

    /// <summary>Why the roadmap could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>Every agent the roadmap names, once each.</summary>
    public IReadOnlyList<AgentName> Agents { get; private set; } = [];

    /// <summary>Reads the roadmap and where its items stand.</summary>
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
            await broker.RoadmapAsync(Id, token).ConfigureAwait(false)).ConfigureAwait(false);

        if (!read.Reachable)
        {
            Problem = read.Problem;

            return Page();
        }

        if (read.Value is not { } roadmap)
        {
            Problem = "There is no such roadmap in this project.";

            return Page();
        }

        // Without the wait-for graph an item reads "could not be read", which is said as that and never as not started.
        var waiting = await BrokerDirectory.ReadAsync(async () =>
            await broker.WaitingAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        Roadmap = roadmap;
        Standing = ItemStates.Of(roadmap, waiting.Value, PersonSeat.On(project.Name));
        Agents = [.. roadmap.Items.Where(i => !i.Removed).Select(i => i.IntendedAgent).OfType<AgentName>().Distinct()];

        return Page();
    }

    /// <summary>Where an item's conversation is, with its text and agents ready when it has not started.</summary>
    /// <param name="state">The item.</param>
    /// <returns>A path on this console.</returns>
    public string OpenPath(ItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = $"/Conversations/Detail?project={Uri.EscapeDataString(Project ?? string.Empty)}"
            + $"&id={Uri.EscapeDataString(state.Item.ConversationName)}";

        if (state.Item.Facts is { ConversationExists: true })
        {
            return path;
        }

        return path
            + $"&Says={Uri.EscapeDataString(state.Item.Text)}"
            + string.Concat(Agents.Select(a => $"&With={Uri.EscapeDataString(a.Value)}"));
    }
}
