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
/// with no link behind it. A send that reaches nobody takes the link back, and every field stays.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="seats">The seats this console holds.</param>
/// <param name="notices">What the console tells the person.</param>
[RequestSizeLimit(Parley.Web.Pages.Projects.FilesModel.PlaceLimitBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = Parley.Web.Pages.Projects.FilesModel.PlaceLimitBytes)]
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

    /// <summary>What the person pasted or dropped into the box, placed once the first words have started the conversation.</summary>
    [BindProperty, FromForm]
    public IList<IFormFile> Attachments { get; set; } = [];

    /// <summary>Files placed, each as its name and hash, to name in the message that follows the first words.</summary>
    /// <remarks>Filled only while sending: a spin-off that reached nobody placed nothing, so nothing is carried over.</remarks>
    public IList<string> Attached { get; } = [];

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
        var opened = Quoting.Opened(name, Id, Says);

        foreach (var target in Targets)
        {
            sent.Add((target, AgentName.TryCreate(target, out var agent)
                ? await voice.RequestAsync(agent, opened, name, token).ConfigureAwait(false)
                : "That is not a name an agent can have."));
        }

        foreach (var (who, outcome) in sent)
        {
            notices.Add(project.Name.Value, name, who, outcome, outcome.StartsWith("Sent.", StringComparison.Ordinal));
        }

        var reached = sent.Where(result => result.Item2.StartsWith("Sent.", StringComparison.Ordinal)).Select(result => result.Item1).ToList();

        // Once it reached anybody the conversation exists, so this form could only be refused the name if it were sent again.
        // What became of anybody it did not reach is in the bell.
        if (reached.Count > 0)
        {
            await SendFilesAfterTheFirstWordsAsync(project, voice, name, reached, token).ConfigureAwait(false);

            return RedirectToPage("/Conversations/Detail", new { project = project.Name.Value, id = name });
        }

        if (Attachments.Count > 0)
        {
            Problem = "Nobody took your words, so the files you attached were not placed. Attach them again when you send.";
        }

        // Reached nobody, so nothing was started: no record says it was, and the name stays free for another try.
        _ = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project)).AbandonSpinOffAsync(Id, name, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        Sent = sent;

        return Page();
    }

    /// <summary>Places the attached files in the new conversation, then names them to everybody who took its first words.</summary>
    /// <param name="project">The project.</param>
    /// <param name="voice">The person's voice.</param>
    /// <param name="name">The new conversation.</param>
    /// <param name="reached">Who took the first words.</param>
    /// <param name="token">Cancels the work.</param>
    /// <returns>When done.</returns>
    /// <remarks>
    /// A conversation that does not exist yet has nowhere to hold a file, so its words go first (SPEC.md §9.5), as on the
    /// conversation page. The page then moves to the conversation, so what went wrong here is said in the bell.
    /// </remarks>
    private async Task SendFilesAfterTheFirstWordsAsync(
        ProjectSettings project, SeatVoice voice, string name, List<string> reached, CancellationToken token)
    {
        if (Attachments.Count == 0)
        {
            return;
        }

        var named = await ComposerFiles
            .PlaceAndNameAsync(
                new BrokerClient(directory.ClientFor(project)), voice, name, Attachments, Attached,
                ComposerFiles.AgentsNamed(reached), token)
            .ConfigureAwait(false);

        if (named.Problem is { } problem)
        {
            notices.Add(project.Name.Value, name, "Files", $"Your words went, and the files did not. {problem}", false);
            return;
        }

        foreach (var (agent, said, sent) in named.Told)
        {
            notices.Add(project.Name.Value, name, agent.Value, said, sent);
        }
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
