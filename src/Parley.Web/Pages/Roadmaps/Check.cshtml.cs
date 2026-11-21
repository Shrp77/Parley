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
[RequestSizeLimit(Parley.Web.Pages.Projects.FilesModel.PlaceLimitBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = Parley.Web.Pages.Projects.FilesModel.PlaceLimitBytes)]
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

    /// <summary>What is sent: the page's words until the person changes them.</summary>
    /// <remarks>A message of the person's, so it is theirs to change before it goes (SPEC.md §5.6).</remarks>
    [BindProperty, FromForm]
    public string? Says { get; set; }

    /// <summary>What the person pasted or dropped into the box, placed once the words have started the conversation.</summary>
    [BindProperty, FromForm]
    public IList<IFormFile> Attachments { get; set; } = [];

    /// <summary>Files placed, each as its name and hash, to name in the message that follows the words.</summary>
    public IList<string> Attached { get; } = [];

    /// <summary>The words the page offers, before the person changes them.</summary>
    public string? Offered { get; private set; }

    /// <summary>What became of each Request that was not taken, when none was.</summary>
    public IReadOnlyList<(string Agent, string Outcome)> Refused { get; private set; } = [];

    /// <summary>Why this could not be drawn or done.</summary>
    public string? Problem { get; private set; }

    /// <summary>Says what asking would do. Nothing is sent here.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is not { } project)
        {
            return RedirectToPage("/Index");
        }

        await ReadAsync(project).ConfigureAwait(false);
        Says = Offered;

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

        if (Roadmap is null || Agents.Count == 0)
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Says))
        {
            Problem = "Nothing was written, so nothing was sent.";

            return Page();
        }

        var seated = await PersonSeats.EnsureAsync(seats, directory, project, token).ConfigureAwait(false);

        if (seated.Hold is not { } hold)
        {
            Problem = $"Nothing was sent: {seated.Problem}";

            return Page();
        }

        // One conversation per check, to the second, so each is read on its own and an earlier one is never added to.
        var conversation = Prefix(Roadmap.Number) + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var voice = new SeatVoice(directory.AgentsFor(project), hold.Seat);
        var reached = new List<AgentName>();
        var refused = new List<(string, string)>();

        foreach (var agent in Agents)
        {
            // The sweep can release the seat while this runs, and nothing is sent under a name the book no longer holds.
            if (hold.Lost)
            {
                refused.Add((agent.Value, "Not sent: this console's seat was released while it was sending."));
                continue;
            }

            var said = await voice.RequestAsync(agent, Says, conversation, token).ConfigureAwait(false);

            if (said.StartsWith("Sent.", StringComparison.Ordinal))
            {
                reached.Add(agent);
            }
            else
            {
                refused.Add((agent.Value, said));
            }
        }

        foreach (var (who, outcome) in refused)
        {
            notices.Add(project.Name.Value, conversation, who, outcome, false);
        }

        if (reached.Count == 0)
        {
            Refused = refused;
            Problem = Attachments.Count > 0
                ? "Nobody took your words, so the files you attached were not placed. Attach them again when you send."
                : "Nobody took your words, so nothing was started.";

            return Page();
        }

        notices.Add(
            project.Name.Value, conversation, "Roadmap check",
            string.Create(CultureInfo.InvariantCulture, $"Asked {reached.Count} of {Agents.Count} agents about roadmap #{Roadmap.Number}."),
            refused.Count == 0);

        await ComposerFiles.SendAfterWordsAsync(
            new BrokerClient(directory.ClientFor(project)), voice, notices, project.Name.Value, conversation,
            Attachments, Attached, reached, token).ConfigureAwait(false);

        return RedirectToPage("/Conversations/Detail", new { project = project.Name.Value, id = conversation });
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
        Offered = Words(roadmap, ItemStates.Of(roadmap, waiting.Value, PersonSeat.On(project.Name)));

        if (Agents.Count == 0)
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
            .Append(ItemsHeading);

        return Items(says, standing).ToString().TrimEnd();
    }

    /// <summary>What the name of every conversation a check of one roadmap opened begins with.</summary>
    /// <param name="roadmapNumber">The roadmap's number.</param>
    /// <returns>The beginning of the name.</returns>
    /// <remarks>
    /// Only how the roadmap page finds them to offer. A name is not authority: anybody may open a conversation under such a
    /// name, and what is done with one is shown to the person word for word before anything is sent.
    /// </remarks>
    internal static string Prefix(int roadmapNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"roadmap-{roadmapNumber}-check-");

    /// <summary>The line above the items in a message that carries them.</summary>
    internal const string ItemsHeading = "Items, in order (number, what it says, who it is for, what it needs, where it stands now):\n";

    /// <summary>Writes a roadmap's live items, one a line, for a message of the person's that carries them.</summary>
    /// <param name="says">What is being written.</param>
    /// <param name="standing">Where each live item stands.</param>
    /// <returns><paramref name="says"/>.</returns>
    internal static StringBuilder Items(StringBuilder says, RoadmapStanding standing)
    {
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

            _ = says.Append(" - ").Append(Stands(state)).Append('\n');
        }

        return says;
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
