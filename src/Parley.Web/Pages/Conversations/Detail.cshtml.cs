using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client;
using Parley.Client.Views;
using Parley.Web.Seating;

namespace Parley.Web.Pages.Conversations;

/// <summary>
/// One conversation, as a chat transcript that says who saw what.
/// </summary>
/// <remarks>
/// The view this feature exists for. It is a chat because that is the form a person reads
/// fastest, and it is annotated because a plain thread would say that every participant read
/// every message — which is the opposite of what SPEC.md §5.0 describes, and would have the
/// operator reasoning about a disagreement neither agent could have had.
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="seats">The seats this console is holding, for the composer's voice.</param>
/// <param name="notices">What the console tells the person, which the bell lists.</param>
public sealed class DetailModel(BrokerDirectory directory, SeatBook seats, Parley.Web.Notifications.NotificationLog notices) : PageModel
{
    /// <summary>Which project this conversation belongs to.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which conversation.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    /// <summary>The messages, in the order the broker recorded them.</summary>
    public IReadOnlyList<Message> Messages { get; private set; } = [];

    /// <summary>What is left of it, when an operator destroyed it.</summary>
    /// <remarks>
    /// Null for every conversation that still exists, and for an identifier that never named one.
    /// A destroyed conversation and one that never existed are different answers, and an operator
    /// who cannot tell them apart cannot tell a destroy from a store that lost something.
    /// </remarks>
    public DestroyedConversation? Destroyed { get; private set; }

    /// <summary>Everyone party to any exchange in it.</summary>
    public IReadOnlyList<AgentName> Participants { get; private set; } = [];

    /// <summary>The project's agents that have not taken part, offered beside the ones that have.</summary>
    /// <remarks>
    /// <see cref="Parley.Client.Rendering.RoomRoster.NotInIt"/>, the one rule the terminal's room reads too, so the page and
    /// the room cannot disagree about who is offered. Ticking one and sending addresses them, which brings them in.
    /// </remarks>
    public IReadOnlyList<AgentName> NotInIt { get; private set; } = [];

    /// <summary>Where each exchange stands, by identifier.</summary>
    public IReadOnlyDictionary<long, ExchangeSnapshot> Exchanges { get; private set; } =
        new Dictionary<long, ExchangeSnapshot>();

    /// <summary>What the registry says about each participant, by name.</summary>
    /// <remarks>
    /// Read beside the transcript so the roster can say whether an agent is running, startable
    /// or stranded. A participant is derived from the traffic (§5.0) and may have been retired
    /// or purged since it spoke, so a name in this conversation is not a promise of a row in the
    /// registry — anything missing is shown as unknown rather than invented.
    /// </remarks>
    public IReadOnlyDictionary<string, AgentRecord> Roll { get; private set; } =
        new Dictionary<string, AgentRecord>(StringComparer.Ordinal);

    /// <summary>What is waiting on the person here, one entry per agent, as the terminal's room lists it.</summary>
    /// <remarks>
    /// <see cref="Parley.Client.Seating.WaitingOnYou"/>, the one derivation both surfaces read, so the page and the room
    /// cannot disagree about who is waiting on the person or how many questions each has.
    /// </remarks>
    public IReadOnlyList<WaitingOn> WaitingOnYou { get; private set; } = [];

    /// <summary>The start of a question, short enough for one line of the list.</summary>
    /// <param name="question">The question.</param>
    /// <returns>Up to a hundred characters, on one line, with an ellipsis when cut.</returns>
    public static string Start(WaitingQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        var flat = string.Join(' ', question.Asked.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return flat.Length <= 100
            ? flat
            : string.Concat(flat.AsSpan(0, char.IsHighSurrogate(flat[98]) ? 98 : 99), "\u2026");
    }

    /// <summary>What each participant is doing, as the terminal's room says it.</summary>
    public IReadOnlyDictionary<AgentName, Activity> ParticipantActivity { get; private set; } =
        new Dictionary<AgentName, Activity>();

    /// <summary>What a participant is doing, in the words and mark both surfaces use, or null when there is nothing to say.</summary>
    /// <param name="who">The participant.</param>
    /// <returns>For example <c>■ stopped 2h48m — 1 permission refused</c>.</returns>
    public string? Doing(AgentName who) =>
        ParticipantActivity.TryGetValue(who, out var doing) && Activities.Says(doing, DateTimeOffset.UtcNow) is { Length: > 0 } says
            ? $"{Activities.Mark(doing)} {says}"
            : null;

    /// <summary>Why this project could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>When the broker answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>Who the operator is writing to.</summary>
    [BindProperty]
    public IList<string> Targets { get; set; } = [];

    /// <summary>Whether to write to everybody in this conversation instead of a chosen few.</summary>
    /// <remarks>
    /// It expands to <see cref="Participants"/> — this conversation's, not the project's. The
    /// terminal's equivalent is project-wide because that is where it is offered, from the list of
    /// conversations rather than from inside one. The two surfaces are not disagreeing: each offers
    /// everybody in the thing it is looking at.
    /// </remarks>
    [BindProperty]
    public bool Everyone { get; set; }

    /// <summary>What the operator wrote.</summary>
    /// <remarks>
    /// Bound on a POST only, so a link carrying <c>Says=</c> opens an empty box: what is in the box is only
    /// ever what the person typed.
    /// </remarks>
    [BindProperty]
    public string? Says { get; set; }

    /// <summary>The exchange this is an answer to, when the person chose to reply to an agent's question.</summary>
    /// <remarks>
    /// Chosen by the person, never inferred (SPEC.md 5.6): the button is on the question being answered. Without it the
    /// words are a new Request, as everything else a person writes is.
    /// </remarks>
    [BindProperty]
    public long? ReplyTo { get; set; }

    /// <summary>Whether a message is an agent's question back to the person that is still waiting on them.</summary>
    /// <param name="message">The message.</param>
    /// <returns><see langword="true"/> for a Clarify to the person, on an open exchange, with nothing after it there.</returns>
    public bool AwaitsReply(Message message) =>
        message.Type == MessageType.Clarify
        && message.To.Role == "human-user"
        && Exchanges.TryGetValue(message.ExchangeId, out var snapshot)
        && snapshot.State == ExchangeState.Open
        && Messages.Last(m => m.ExchangeId == message.ExchangeId).Id == message.Id;

    /// <summary>Words a link suggests, shown as the box's hint and never put in it.</summary>
    /// <remarks>
    /// A roadmap item opens its conversation with its own text as the hint, so the person sees what the item is and
    /// writes their own words. A hint is not something they have said: the box stays empty until they type.
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public string? Hint { get; set; }

    /// <summary>The agents a link ticked for the person, so they can untick before sending.</summary>
    /// <remarks>Only a head start on <see cref="Targets"/>: it is read on a GET and nothing is addressed until Send.</remarks>
    [BindProperty(SupportsGet = true)]
    public IList<string> With { get; set; } = [];

    /// <summary>The seat this person holds in this project, or null if they hold none.</summary>
    /// <remarks>
    /// The composer always speaks as the person. A surface without a seat has no name in the
    /// registry to send as, and the console must not invent one — SPEC.md §13 rests on a name belonging to
    /// whoever launched the process claiming it.
    /// </remarks>
    public SeatHold? Seat { get; private set; }

    /// <summary>What became of each message, once any were sent.</summary>
    public IReadOnlyList<(string Agent, string Outcome)> Sent { get; private set; } = [];

    /// <summary>Why nothing could be sent, when nothing could.</summary>
    public string? SendProblem { get; private set; }

    /// <summary>The messages as the page draws them: a send to several agents is one turn, not one per agent.</summary>
    /// <returns>Each turn, in the order the broker recorded its first message.</returns>
    /// <remarks>
    /// A send to several agents is recorded as one Request each, because a refusal is per agent. The words, the sender
    /// and the conversation are the same, and the recipients are what the "to" line is for, so adjacent Requests that
    /// match are drawn once with every recipient named. Only adjacent ones: reordering messages to join them would
    /// misstate the order the broker recorded.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<Message>> Turns()
    {
        var turns = new List<List<Message>>();

        foreach (var message in Messages)
        {
            if (turns.Count > 0
                && turns[^1][0] is { Type: MessageType.Request } first
                && message.Type == MessageType.Request
                && first.From == message.From
                && first.To != message.To
                && string.Equals(first.PayloadJson, message.PayloadJson, StringComparison.Ordinal)
                && turns[^1].All(m => m.To != message.To))
            {
                turns[^1].Add(message);
            }
            else
            {
                turns.Add([message]);
            }
        }

        return [.. turns.Select(turn => (IReadOnlyList<Message>)turn)];
    }

    /// <summary>Says where the exchange a message belongs to stands.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The state in words, or empty when the export did not include the exchange.</returns>
    /// <remarks>
    /// Read from the export rather than inferred from the messages, because an exchange a person closed
    /// and one that was finished can carry the same last message — and an operator opens a conversation
    /// precisely because something went wrong.
    /// </remarks>
    public string StateOf(Message message) =>
        Exchanges.TryGetValue(message.ExchangeId, out var snapshot)
            ? snapshot.State == ExchangeState.Open ? "still open, awaiting a result" : Reading.State(snapshot)
            : string.Empty;

    /// <summary>What a reply settled (SPEC.md §5.6), from the record rather than its words.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The sentence, or empty when it settled nothing.</returns>
    public string SettledBy(Message message) =>
        Reading.Settled(message, Whole) is { Count: > 0 } settled ? Reading.SettledLine(settled) : string.Empty;

    /// <summary>The conversation as one export, for the derivations that read messages and exchanges together.</summary>
    private ConversationExport Whole => _whole ??= new ConversationExport([.. Messages], [.. Exchanges.Values], null);

    private ConversationExport? _whole;

    /// <summary>Whether the exchange a message belongs to was closed by a person rather than finished.</summary>
    /// <param name="message">The message.</param>
    /// <returns><see langword="true"/> when its exchange closed with outcome <c>closed</c>.</returns>
    public bool ClosedUnfinished(Message message) =>
        Exchanges.TryGetValue(message.ExchangeId, out var snapshot) && snapshot.Outcome == ExchangeOutcome.Closed;

    /// <summary>Where a following page asks for the conversation again.</summary>
    public string FollowPath =>
        $"/Conversations/Detail?project={Uri.EscapeDataString(Project ?? string.Empty)}"
        + $"&id={Uri.EscapeDataString(Id ?? string.Empty)}&handler=Follow";

    /// <summary>A digest of a rendered region, which a following page compares to decide whether anything changed.</summary>
    /// <param name="html">The region as rendered.</param>
    /// <returns>SHA-256 of its UTF-8 bytes, in lowercase hex.</returns>
    public static string Digest(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(html)));
    }

    /// <summary>The conversation's changing region again, for a page that is following it.</summary>
    /// <returns>
    /// The region, or 503 carrying the reason when the broker could not be read, or 404 for a project this
    /// console does not know.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The transcript follows its conversation.</b> A transcript grows only at its end, and
    /// the page keeps the message a reader is looking at where it was, so a page never changes under a reader
    /// mid-sentence, and a reader at the bottom sees an answer arrive without reloading.
    /// </para>
    /// <para>
    /// <b>The whole region rather than the messages after one the reader has.</b> A new message is not the
    /// only change: an earlier exchange closes, a participant's standing in the roster moves, a note about
    /// who could see a message gains a name. The terminal reads the whole export and redraws when the text
    /// differs, and this is that, with the page comparing digests so nothing is touched when nothing changed.
    /// </para>
    /// <para>
    /// <b>A read that follows must not keep a seat.</b> The frame touches a seat on every render, because a
    /// page render is evidence somebody is at the console and <see cref="SeatBook.IdleAfter"/> is the only
    /// thing that releases a seat whose person has gone. A tab polling every few seconds is not a person, and
    /// a follow that counted would hold a seat for as long as a browser stayed open — which is the case the
    /// idle release exists for. So this returns a partial, which has no frame, and reads no seat.
    /// </para>
    /// <para>
    /// A failed read changes nothing on the page: it answers 503 and the page keeps what it shows and says it
    /// may be out of date. A 200 with an empty region would replace a conversation with nothing because of a
    /// moment's trouble reaching the broker.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnGetFollowAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return NotFound();
        }

        await ReadAsync(project, Id).ConfigureAwait(false);

        Response.Headers.CacheControl = "no-store";

        return Problem is null ? Partial("_Followed", this) : Unavailable(Problem);
    }

    /// <summary>The transcript as text, with its annotations intact.</summary>
    /// <returns>A file the operator can take away.</returns>
    /// <remarks>
    /// <para>
    /// The annotations travel with it. An export that dropped them would be the misleading
    /// artefact the whole view exists to prevent, one step further from anybody who could catch
    /// it — read somewhere else, by somebody who never saw the page.
    /// </para>
    /// <para>
    /// A broker that did not answer produces no file at all. Writing an empty transcript would be
    /// the same artefact by a different route: a plausible document, saved and filed, in which a
    /// conversation that happened appears never to have — and unlike a blank page on screen,
    /// nothing about the file says the read failed.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnGetTranscriptAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ExportAsync(Id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (read.Value is not { } export)
        {
            return Unavailable(read.Problem);
        }

        return File(
            System.Text.Encoding.UTF8.GetBytes(
                Transcript.Write(Id, Project, export.Messages, export.Exchanges)),
            "text/plain; charset=utf-8",
            $"{Id}.txt");
    }

    /// <summary>The messages as the operator surface returned them, for machine use.</summary>
    /// <returns>A file the operator can take away.</returns>
    /// <remarks>
    /// Refuses on an unreadable project for the same reason as the transcript, and it matters
    /// more here: this one is what something else parses, and empty arrays are a valid answer
    /// meaning the conversation had no messages.
    /// </remarks>
    public async Task<IActionResult> OnGetRawAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ExportAsync(Id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (read.Value is not { } export)
        {
            return Unavailable(read.Problem);
        }

        // A file and not a page: a JSON body answered to a link replaced the console in the tab, with no way back.
        return File(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { conversation = Id, messages = export.Messages, exchanges = export.Exchanges },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                }),
            "application/json",
            $"{Id}.json");
    }

    /// <summary>Keys exchange snapshots by their identifier, tolerating a repeated one.</summary>
    /// <param name="exchanges">What the export returned.</param>
    /// <returns>One snapshot per identifier.</returns>
    /// <remarks>
    /// <c>ToDictionary</c> throws on a duplicate key, and the throw would be outside what
    /// <see cref="BrokerDirectory.ReadAsync{T}"/> contains — so a broker that returned the same
    /// exchange twice would not produce a page saying so, it would produce no page at all. That
    /// is a broker defect either way; the difference is whether the operator can see it or is
    /// looking at a blank error while the deployment it describes carries on.
    /// </remarks>
    private static IReadOnlyDictionary<long, ExchangeSnapshot> Snapshots(
        IReadOnlyList<ExchangeSnapshot> exchanges)
    {
        var keyed = new Dictionary<long, ExchangeSnapshot>();

        foreach (var exchange in exchanges)
        {
            keyed[exchange.ExchangeId] = exchange;
        }

        return keyed;
    }

    /// <summary>Sends the operator's words to the agents they picked.</summary>
    /// <returns>The page, reporting what became of each.</returns>
    /// <remarks>
    /// <para>
    /// One Request per agent, because a refusal is per agent: a pair at its cap, or a close that would make a cycle,
    /// says nothing about the others, and a single "sent" would hide the case the person needs to see. It is always
    /// the person's voice: the message is in the conversation like any other, attributed to the seat, and every §7
    /// refusal applies to it unchanged.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        // **Everybody, resolved here rather than by checking boxes in a browser.** The alternative
        // is script that ticks them, which would make the one composer that sends to several
        // agents stop working without script.
        //
        // It is read after the page is loaded because the roster it expands to has to be the one
        // the server holds now, not the one the form was rendered from: a participant who joined
        // between the render and the post is in this conversation, and "everybody" that quietly
        // meant "everybody a minute ago" is the kind of thing nobody notices until it matters.
        if (Everyone)
        {
            await OnGetAsync().ConfigureAwait(false);

            Targets = [.. Participants.Select(who => who.Value)];
        }

        if ((ReplyTo is null && Targets.Count == 0) || string.IsNullOrWhiteSpace(Says))
        {
            SendProblem = ReplyTo is null && Targets.Count == 0
                ? "Nobody was picked, so nothing was sent."
                : "Nothing was written, so nothing was sent.";

            await OnGetAsync().ConfigureAwait(false);

            return Page();
        }

        var token = HttpContext.RequestAborted;
        var sent = new List<(string, string)>();

        // **Always the person's voice.** The broker provides the seat and this takes it; when it
        // cannot be taken, nothing is sent and the reason is said.
        var seated = await PersonSeats.EnsureAsync(seats, directory, project, token).ConfigureAwait(false);

        if (seated.Hold is not { } hold)
        {
            SendProblem = $"Nothing was sent: {seated.Problem}";

            await OnGetAsync().ConfigureAwait(false);

            return Page();
        }

        // **A reply the person chose to give to a question**, on that exchange, so it answers the question rather than
        // standing beside it as a new Request that leaves the question open. The broker checks the exchange is theirs to
        // answer.
        if (ReplyTo is { } exchange)
        {
            var answered = await new SeatVoice(directory.AgentsFor(project), hold.Seat)
                .ClarifyAsync(hold, exchange, Says, token).ConfigureAwait(false);

            Sent = [("Reply", answered.Says)];
            Tell(project.Name.Value, "Reply", answered.Says);

            if (answered.Closed)
            {
                Says = null;
            }

            ReplyTo = null;

            await OnGetAsync().ConfigureAwait(false);

            return Page();
        }

        // The person is never one of their own recipients. "Everyone in this conversation" includes the seat that is
        // sending, and the broker rightly refuses a request to oneself, which read as a failure and kept the words in the box.
        var others = Targets.Where(target => !string.Equals(target, hold.Seat.Name.Value, StringComparison.Ordinal)).ToList();

        if (others.Count == 0)
        {
            SendProblem = "Nobody was picked, so nothing was sent.";

            await OnGetAsync().ConfigureAwait(false);

            return Page();
        }

        Targets = others;

        var voice = new SeatVoice(directory.AgentsFor(project), hold.Seat);

        // **The first words on a roadmap item say what it is about.** The agent was never in the roadmap and cannot see it,
        // so a bare "go ahead" on rm1-3 means nothing to it. Only when the conversation is new and was read cleanly: a
        // conversation that already has messages has given the agent its context, and a failed read must not be taken
        // for an empty conversation.
        var said = await OpeningAsync(project, Id, Says).ConfigureAwait(false);

        foreach (var target in Targets)
        {
            if (!AgentName.TryCreate(target, out var agent))
            {
                sent.Add((target, "That is not a name an agent can have."));
                continue;
            }

            sent.Add((target, await voice
                .RequestAsync(agent, said, Id, token)
                .ConfigureAwait(false)));
        }

        Sent = sent;

        foreach (var (who, outcome) in sent)
        {
            Tell(project.Name.Value, who, outcome);
        }

        // Cleared once it has gone to everybody. When any was refused the words stay, so they can be fixed or sent again
        // without being typed twice.
        if (sent.All(result => result.Item2.StartsWith("Sent.", StringComparison.Ordinal)))
        {
            Says = null;
        }

        await OnGetAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Keeps what the broker answered, for the bell.</summary>
    /// <param name="project">The project.</param>
    /// <param name="who">Who it is about.</param>
    /// <param name="outcome">What the broker said.</param>
    private void Tell(string project, string who, string outcome) =>
        notices.Add(project, Id, who, outcome, outcome.StartsWith("Sent.", StringComparison.Ordinal));

    /// <summary>The person's words, led by the roadmap item they are about when this opens the work on it.</summary>
    /// <param name="project">The project.</param>
    /// <param name="id">The conversation.</param>
    /// <param name="words">What the person typed.</param>
    /// <returns>The words to send.</returns>
    private async Task<string> OpeningAsync(ProjectSettings project, string id, string words)
    {
        await ReadAsync(project, id).ConfigureAwait(false);
        await ReadItemTitleAsync(project).ConfigureAwait(false);

        return Problem is null && Messages.Count == 0 && Item is { Removed: false } && ItemTitle is { Length: > 0 } item
            ? Lead(item, RoadmapTitle, words)
            : words;
    }

    /// <summary>The first words of a message that opens the work on a roadmap item.</summary>
    /// <param name="item">The item's words.</param>
    /// <param name="roadmap">The roadmap's title.</param>
    /// <param name="words">What the person typed.</param>
    /// <returns>The words led by what they are about.</returns>
    /// <remarks>Plain text, because a payload is inert data and the agent reads it as the person's own line.</remarks>
    public static string Lead(string item, string? roadmap, string words) =>
        $"This is about \"{item}\" in the roadmap \"{roadmap}\": {words}";

    /// <summary>Says the project could not be read, rather than producing an empty export.</summary>
    /// <param name="problem">What the read reported.</param>
    /// <returns>A refusal carrying the reason.</returns>
    private ObjectResult Unavailable(string? problem) =>
        new(problem ?? "This project's broker did not answer.")
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };

    /// <summary>Reads the conversation.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        // The frame has already taken the seat on this render (PersonSeats); this looks it up for the composer.
        Seat = seats.Held(project.Name.Value);

        await ReadAsync(project, Id).ConfigureAwait(false);
        await ReadItemTitleAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>The broker's record that this conversation is a roadmap item's, or <see langword="null"/>.</summary>
    /// <remarks>
    /// From the reservation the export carries, never from the conversation's name or a message (SPEC.md §5.7), so an
    /// agent cannot make a conversation look like an item's. A removed item's conversation keeps it, marked removed.
    /// </remarks>
    public RoadmapItemRef? Item { get; private set; }

    /// <summary>The words of the roadmap item this conversation is the work on, or <see langword="null"/>.</summary>
    /// <remarks>So a person who is inside <c>rm1-3</c> is told what rm1-3 is.</remarks>
    public string? ItemTitle { get; private set; }

    /// <summary>The title of the roadmap that item is in, or <see langword="null"/>.</summary>
    public string? RoadmapTitle { get; private set; }

    /// <summary>Where the roadmap this conversation's item belongs to is, opened on that item; null for an ordinary one.</summary>
    public string? RoadmapPath => Item is { } item
        ? string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"/Roadmaps/Roadmap?project={Uri.EscapeDataString(Project ?? string.Empty)}&id={item.RoadmapId}&item={item.ItemNumber}#item-{item.ItemNumber}")
        : null;

    /// <summary>Reads the words of the item this conversation's reservation names.</summary>
    /// <param name="project">The project.</param>
    /// <returns>When it has been looked for. A broker that cannot answer leaves the words out and costs nothing else.</returns>
    /// <remarks>Read after <see cref="ReadAsync"/>, whose export says whether the conversation is an item's at all.</remarks>
    private async Task ReadItemTitleAsync(ProjectSettings project)
    {
        if (Item is not { } item)
        {
            return;
        }

        RoadmapTitle = item.Title;

        var broker = new BrokerClient(directory.ClientFor(project));

        var found = await BrokerDirectory.ReadAsync(async () =>
            await broker.RoadmapAsync(item.RoadmapId, HttpContext.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);

        ItemTitle = found.Value?.Items.FirstOrDefault(i => i.Number == item.ItemNumber)?.Text;
    }

    /// <summary>Reads the conversation and its participants' standing, for the page or for a follow.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="id">The conversation.</param>
    /// <returns>When it has been read.</returns>
    private async Task ReadAsync(ProjectSettings project, string id)
    {
        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ExportAsync(id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        Messages = read.Value?.Messages ?? [];
        Destroyed = read.Value?.Destroyed;
        Item = read.Value?.Item;
        Participants = Visibility.Participants(Messages);
        Exchanges = Snapshots(read.Value?.Exchanges ?? []);
        Problem = read.Problem;

        // A second read, contained separately: the transcript is the page, and a registry that
        // did not answer must cost the roster's status column rather than the conversation.
        if (read.Value is not null)
        {
            var roll = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .RegistryAsync(HttpContext.RequestAborted).ConfigureAwait(false))
                .ConfigureAwait(false);

            var known = new Dictionary<string, AgentRecord>(StringComparer.Ordinal);

            foreach (var agent in roll.Value ?? [])
            {
                known[agent.Name.Value] = agent;
            }

            Roll = known;
            NotInIt = Parley.Client.Rendering.RoomRoster.NotInIt(roll.Value, Participants, PersonSeat.On(project.Name));

            // **What each participant is doing, from the same derivation as the terminal's room**:
            // working only while a turn runs, stopped with what it was refused, waiting on somebody. Read here, which is
            // the follow's path too, so it refreshes with the transcript rather than freezing at page load.
            var waiting = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .WaitingAsync(HttpContext.RequestAborted).ConfigureAwait(false))
                .ConfigureAwait(false);

            ParticipantActivity = Activities.Of(read.Value, waiting.Value ?? [], roll.Value);
            WaitingOnYou = Parley.Client.Seating.WaitingOnYou.From(
                read.Value, waiting.Value ?? [], PersonSeat.On(project.Name));
        }
        AsOf = DateTimeOffset.Now;
    }
}
