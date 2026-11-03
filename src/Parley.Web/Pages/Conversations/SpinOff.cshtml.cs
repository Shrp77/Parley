using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Web.Seating;

namespace Parley.Web.Pages.Conversations;

/// <summary>Starting a new conversation from one the person is reading (SPEC.md §5.0, <c>spin_off</c>).</summary>
/// <remarks>
/// <para>
/// <b>A page of its own</b>, so what is ticked here is only what this page says: the conversation page remembers ticks per
/// conversation in the browser, and none of that may decide who a spin-off goes to.
/// </para>
/// <para>
/// <b>Nothing happens until Send.</b> The link is recorded first and the Request sent after, so a Request can never arrive
/// with no link behind it. A send that is refused leaves an empty conversation no list shows, and every field stays.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="seats">The seats this console holds.</param>
/// <param name="notices">What the console tells the person.</param>
public sealed class SpinOffModel(BrokerDirectory directory, SeatBook seats, Parley.Web.Notifications.NotificationLog notices) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The conversation this comes from.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    /// <summary>The message it starts from, or null to start from the conversation as a whole.</summary>
    /// <remarks>Only ever a number: the words are read from the broker's record, and a link cannot supply them.</remarks>
    [BindProperty(SupportsGet = true)]
    public long? From { get; set; }

    /// <summary>What the new conversation is called.</summary>
    [BindProperty, FromForm]
    public string? Name { get; set; }

    /// <summary>What goes in its first message.</summary>
    [BindProperty, FromForm]
    public string? Says { get; set; }

    /// <summary>Who it goes to.</summary>
    [BindProperty, FromForm]
    public IList<string> Targets { get; set; } = [];

    /// <summary>The project's agents, to choose from.</summary>
    public IReadOnlyList<AgentName> Agents { get; private set; } = [];

    /// <summary>Why this could not be drawn or done.</summary>
    public string? Problem { get; private set; }

    /// <summary>What became of each message, when not all went.</summary>
    public IReadOnlyList<(string Agent, string Outcome)> Sent { get; private set; } = [];

    /// <summary>Draws the form, with the message quoted and only its author ticked.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is not { } project || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        await ReadAgentsAsync(project).ConfigureAwait(false);

        if (From is not { } from)
        {
            return Page();
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project)).ExportAsync(Id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (read.Value is not { } export)
        {
            Problem = read.Problem;
            return Page();
        }

        if (export.Messages.FirstOrDefault(m => m.Id == from) is not { } message)
        {
            Problem = "That message is not in this conversation any more. It may have moved, or been removed by retention.";
            return Page();
        }

        Says = Quoting.Of(message, Id);
        Targets = Agents.Contains(message.From) ? [message.From.Value] : [];

        return Page();
    }

    /// <summary>Records the link, then sends.</summary>
    /// <returns>The new conversation, or this page with everything kept and what went wrong.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        if (directory.Find(Project) is not { } project || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        var token = HttpContext.RequestAborted;

        await ReadAgentsAsync(project).ConfigureAwait(false);

        var name = Name?.Trim();

        if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(Says) || Targets.Count == 0)
        {
            Problem = string.IsNullOrEmpty(name)
                ? "It needs a name, so nothing was sent."
                : Targets.Count == 0 ? "Nobody was picked, so nothing was sent." : "Nothing was written, so nothing was sent.";

            return Page();
        }

        var seated = await PersonSeats.EnsureAsync(seats, directory, project, token).ConfigureAwait(false);

        if (seated.Hold is not { } hold)
        {
            Problem = $"Nothing was sent: {seated.Problem}";
            return Page();
        }

        var linked = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project)).SpinOffAsync(Id, name, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!linked.Reachable || linked.Value is not null)
        {
            Problem = $"Nothing was sent: {(linked.Reachable ? linked.Value : linked.Problem)}";
            return Page();
        }

        var voice = new SeatVoice(directory.AgentsFor(project), hold.Seat);
        var sent = new List<(string, string)>();

        foreach (var target in Targets)
        {
            sent.Add((target, AgentName.TryCreate(target, out var agent)
                ? await voice.RequestAsync(agent, Says, name, token).ConfigureAwait(false)
                : "That is not a name an agent can have."));
        }

        foreach (var (who, outcome) in sent)
        {
            notices.Add(project.Name.Value, name, who, outcome, outcome.StartsWith("Sent.", StringComparison.Ordinal));
        }

        // Once it reached anybody the conversation exists, so this form could only be refused the name if it were sent again.
        // What became of anybody it did not reach is in the bell.
        if (sent.Exists(result => result.Item2.StartsWith("Sent.", StringComparison.Ordinal)))
        {
            return RedirectToPage("/Conversations/Detail", new { project = project.Name.Value, id = name });
        }

        // Reached nobody, so nothing was started: no record says it was, and the name stays free for another try.
        _ = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project)).AbandonSpinOffAsync(Id, name, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        Sent = sent;

        return Page();
    }

    private async Task ReadAgentsAsync(ProjectSettings project)
    {
        var roll = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project)).RegistryAsync(HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        Problem = roll.Problem;
        Agents = RoomRoster.NotInIt(roll.Value, [], PersonSeat.On(project.Name));
    }
}
