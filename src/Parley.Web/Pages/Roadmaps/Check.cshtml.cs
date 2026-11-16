using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Roadmaps;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Web.Seating;

namespace Parley.Web.Pages.Roadmaps;

/// <summary>
/// Asking every agent in the project how a roadmap stands, having first said what that sends and to whom (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The person's own Request, and no operation of an agent's.</b> The roadmap is the person's and no agent reads one
/// (principle 2.12), so what the agents see is the items' words in a Request the person sends, as when an item is started.
/// Each agent answers in its own Result. Nothing a roadmap holds is changed here: the person reads the answers and edits the
/// roadmap, or does not.
/// </para>
/// <para>
/// <b>The agents are told not to propose a roadmap in answer.</b> Confirming a revision supersedes every other waiting
/// proposal naming that roadmap (§5.7), so every agent proposing one would leave the person one view and the rest
/// discarded; the views are wanted together.
/// </para>
/// <para>
/// <b>A page of its own.</b> The send wakes every agent that is not running, and every agent in the project is asked, so a
/// GET says who and what and only a POST sends.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="seats">The seats this console holds.</param>
/// <param name="notices">What the console tells the person.</param>
public sealed class CheckModel(
    BrokerDirectory directory,
    SeatBook seats,
    Parley.Web.Notifications.NotificationLog notices) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which roadmap.</summary>
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The roadmap, once read.</summary>
    public Roadmap? Roadmap { get; private set; }

    /// <summary>Everybody the Request goes to: every active agent in the project but the person and Parley.</summary>
    public IReadOnlyList<AgentName> Agents { get; private set; } = [];

    /// <summary>What would be sent, word for word.</summary>
    public string? Says { get; private set; }

    /// <summary>The conversation it would open.</summary>
    public string? Conversation { get; private set; }

    /// <summary>Why this could not be drawn or done.</summary>
    public string? Problem { get; private set; }

    /// <summary>What became of the send, when it did not reach anybody.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Says what asking would do. Nothing is sent here.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        await ReadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Sends the Request to every agent.</summary>
    /// <returns>The conversation it opened, or this page with what went wrong.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        var token = HttpContext.RequestAborted;

        await ReadAsync(project).ConfigureAwait(false);

        if (Roadmap is null || Says is null || Conversation is null || Agents.Count == 0)
        {
            return Page();
        }

        var seated = await PersonSeats.EnsureAsync(seats, directory, project, token).ConfigureAwait(false);

        if (seated.Hold is not { } hold)
        {
            Problem = $"Nothing was sent: {seated.Problem}";

            return Page();
        }

        var said = await new SeatVoice(directory.AgentsFor(project), hold.Seat)
            .RequestEveryoneAsync(hold, Agents, Says, Conversation, token).ConfigureAwait(false);

        var reached = said.StartsWith("Sent to ", StringComparison.Ordinal)
            && !said.StartsWith("Sent to 0 ", StringComparison.Ordinal);

        notices.Add(project.Name.Value, Conversation, "Roadmap check", said, reached);

        if (reached)
        {
            return RedirectToPage("/Conversations/Detail", new { project = project.Name.Value, id = Conversation });
        }

        Outcome = said;

        return Page();
    }

    private async Task ReadAsync(ProjectSettings project)
    {
        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        var read = await BrokerDirectory.ReadAsync(async () =>
            await broker.RoadmapAsync(Id, token).ConfigureAwait(false)).ConfigureAwait(false);

        if (!read.Reachable)
        {
            Problem = read.Problem;

            return;
        }

        if (read.Value is not { } roadmap)
        {
            Problem = "There is no such roadmap in this project.";

            return;
        }

        var roll = await BrokerDirectory.ReadAsync(async () =>
            await broker.RegistryAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        if (!roll.Reachable)
        {
            Problem = roll.Problem;

            return;
        }

        // Without the wait-for graph an item reads "could not be read", which is said as that and never as not started.
        var waiting = await BrokerDirectory.ReadAsync(async () =>
            await broker.WaitingAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        Roadmap = roadmap;
        Agents = RoomRoster.NotInIt(roll.Value, [], PersonSeat.On(project.Name));
        Says = Words(roadmap, ItemStates.Of(roadmap, waiting.Value, PersonSeat.On(project.Name)));

        // One conversation per check, so each is read on its own and an earlier one is never added to.
        Conversation = string.Create(
            CultureInfo.InvariantCulture, $"roadmap-{roadmap.Number}-check-{DateTime.Now:yyyyMMdd-HHmm}");

        if (ConversationName.Problem(Conversation) is { } wrong)
        {
            Problem = wrong;
        }
        else if (Agents.Count == 0)
        {
            Problem = "There is no agent in this project to ask.";
        }
    }

    /// <summary>The Request: what is wanted, then the items as they stand.</summary>
    /// <param name="roadmap">The roadmap.</param>
    /// <param name="standing">Where each live item stands.</param>
    /// <returns>The words.</returns>
    /// <remarks>
    /// Short and in a fixed shape, so that the answers can be read side by side. The items are the person's words and
    /// travel as data: an item that reads like an instruction is still an item.
    /// </remarks>
    internal static string Words(Roadmap roadmap, RoadmapStanding standing)
    {
        ArgumentNullException.ThrowIfNull(roadmap);
        ArgumentNullException.ThrowIfNull(standing);

        var says = new StringBuilder()
            .Append("Roadmap check: \"").Append(roadmap.Title).Append("\" (roadmap #")
            .Append(roadmap.Number.ToString(CultureInfo.InvariantCulture)).Append(").\n\n")
            .Append("I am asking every agent in the project how this roadmap stands, from where you stand. ")
            .Append("Please answer in your Result, in short lines, and say \"nothing to add\" where you have nothing:\n")
            .Append("1. Status: for each item you know about, its number, and whether it is done, in progress, blocked (say on what) or not needed.\n")
            .Append("2. Remove: any item that can come out, because it is done elsewhere or no longer needed.\n")
            .Append("3. Add: anything missing, one line led by a verb, with where it belongs (after which item number) and which agent it is for.\n\n")
            .Append("Answer only from what you know. Do not call propose_roadmap for this: I am collecting every agent's view and will change the roadmap myself, so nothing changes until I do.\n\n")
            .Append("Items, in order (number, what it says, who it is for, what it needs, where it stands now):\n");

        foreach (var state in standing.Items)
        {
            var item = state.Item;

            _ = says.Append(item.Number.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(item.Text);

            if (item.IntendedAgent is { } agent)
            {
                _ = says.Append(" - for ").Append(agent.Value);
            }

            if (item.Needs.Count > 0)
            {
                _ = says.Append(" - needs ").Append(string.Join(", ", item.Needs.Order()));
            }

            _ = says.Append(" - ").Append(ItemWording.Standing(state)).Append('\n');
        }

        return says.ToString().TrimEnd();
    }

    /// <summary>Where an item stands, in words that make sense to an agent reading the person's message.</summary>
    /// <param name="state">The item's derived state.</param>
    /// <returns>The words.</returns>
    /// <remarks>
    /// Not <see cref="ItemWording.Standing"/>, which speaks to the person ("waiting on you", and what the person can do about
    /// a block): in the person's message to an agent, "you" would be the agent.
    /// </remarks>
    private static string Stands(ItemState state)
    {
        var holders = string.Join(", ", state.Holders.Select(h => h.Role));
        var blocker = state.Blocker is { Length: > 0 } named ? named.Replace('_', ' ') : null;
        var also = blocker is null ? string.Empty : $"; blocked: {blocker}";

        return state.Standing switch
        {
            ItemStanding.NotStarted => "not started",
            ItemStanding.InProgress => (holders.Length > 0 ? $"in progress with {holders}" : "in progress") + also,
            ItemStanding.WaitingOnAgent => (holders.Length > 0 ? $"waiting on {holders}" : "waiting on an agent") + also,
            ItemStanding.WaitingOnPerson => "waiting on me" + also,
            ItemStanding.Blocked => blocker is null ? "blocked" : $"blocked: {blocker}",
            ItemStanding.Done => "done",
            ItemStanding.Closed => blocker is null ? "closed" : $"closed while blocked: {blocker}",
            _ => "could not be read",
        };
    }
}
