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
public sealed class DetailModel(BrokerDirectory directory, SeatBook seats) : PageModel
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

    /// <summary>What is waiting on the person here, one entry per agent, as the terminal's room lists it (specs/025).</summary>
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

    /// <summary>What each participant is doing, as the terminal's room says it (specs/024).</summary>
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
    [BindProperty]
    public string? Says { get; set; }

    /// <summary>The seat this person holds in this project, or null if they hold none.</summary>
    /// <remarks>
    /// What decides whether the composer speaks or instructs. A person without a seat has no
    /// name in the registry to send as, and the console must not invent one — SPEC.md §13 rests
    /// on a name belonging to whoever launched the process claiming it.
    /// </remarks>
    public SeatHold? Seat { get; private set; }

    /// <summary>What became of each instruction, once any were sent.</summary>
    public IReadOnlyList<(string Agent, string Outcome)> Sent { get; private set; } = [];

    /// <summary>Why nothing could be sent, when nothing could.</summary>
    public string? SendProblem { get; private set; }

    /// <summary>Says who did not see one message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>A sentence, or empty when everybody in the conversation was party to it.</returns>
    public string NotSeenBy(Message message) => Visibility.Describe(message, Participants);

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
    /// <b>The transcript follows its conversation, which reverses what this page used to do.</b> It never
    /// refreshed, on the argument that a page changing under a reader mid-sentence is worse than one briefly
    /// out of date, and said instead that it had fallen behind. The terminal answered that argument in 0.25.7
    /// rather than overruling it, and this answers it the same way: a transcript grows only at its end, and
    /// the page keeps the message a reader is looking at where it was — so what the argument feared does not
    /// happen, and a reader at the bottom sees an answer arrive without reloading.
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

        return new JsonResult(new
        {
            conversation = Id,
            messages = export.Messages,
            exchanges = export.Exchanges
        });
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
    /// One instruction per agent, because §10.2's <c>instruct</c> takes one and the four outcomes
    /// it distinguishes are per agent: a machine that is off makes one recipient undeliverable
    /// and says nothing about the others. Collapsing them into a single "sent" would hide exactly
    /// the case the operator needs to see.
    /// </para>
    /// <para>
    /// <b>Which of two things this does depends on whether a person has joined.</b>
    /// </para>
    /// <para>
    /// Without a seat it instructs, and puts nothing in the conversation. §5.0 derives
    /// participants from being party to an exchange, and there is no operator <c>request</c> — an
    /// operator is not an agent and has no name in the registry to send as.
    /// What it does is tell agents something, marked as the operator's (§11.5); if one of them
    /// then speaks to a peer, that message is theirs and appears here as theirs.
    /// </para>
    /// <para>
    /// With a seat it speaks, and the message is in the conversation like any other. The person
    /// has a registration, an open attachment and an identity of their own (§11.3's
    /// <c>human:&lt;name&gt;</c>), so this is not the console acting on somebody's behalf — it is
    /// the surface through which a participant SPEC.md always admitted finally has a way to take
    /// part. Every §7 refusal applies to them unchanged: saturate a pair and they are refused,
    /// close a cycle and §6 refuses that too.
    /// </para>
    /// <para>
    /// The conversation is named in the text rather than carried in a field, because the wire has
    /// nowhere to put it: an instruction is not part of a conversation and §10.2 gives it no such
    /// parameter. Naming it is the operator telling the agent what they are talking about, which
    /// is content, and the composer shows the exact words before they are sent.
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
        // is script that ticks them, which this console does not use anywhere and would make the
        // one composer that sends to several agents the only page that stops working without it.
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

        if (Targets.Count == 0 || string.IsNullOrWhiteSpace(Says))
        {
            SendProblem = Targets.Count == 0
                ? "Nobody was picked, so nothing was sent."
                : "Nothing was written, so nothing was sent.";

            await OnGetAsync().ConfigureAwait(false);

            return Page();
        }

        var token = HttpContext.RequestAborted;
        var sent = new List<(string, string)>();

        // **Always the person's voice, never the operator's** (specs/023). Until then a composer with no seat behind it
        // fell back to sending an instruction — a different kind of thing, reported differently, and absent from the
        // transcript — so what a person wrote here depended on whether they had remembered to join. The broker provides
        // the seat now and this takes it; when it cannot be taken, nothing is sent and the reason is said.
        var seated = await PersonSeats.EnsureAsync(seats, directory, project, token).ConfigureAwait(false);

        if (seated.Hold is not { } hold)
        {
            SendProblem = $"Nothing was sent: {seated.Problem}";

            await OnGetAsync().ConfigureAwait(false);

            return Page();
        }

        var voice = new SeatVoice(directory.AgentsFor(project), hold.Seat);

        foreach (var target in Targets)
        {
            if (!AgentName.TryCreate(target, out var agent))
            {
                sent.Add((target, "That is not a name an agent can have."));
                continue;
            }

            sent.Add((target, await voice
                .RequestAsync(agent, Says, Id, token)
                .ConfigureAwait(false)));
        }

        Sent = sent;

        await OnGetAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>The exact words each agent receives.</summary>
    /// <returns>The instruction text.</returns>
    /// <remarks>
    /// Shown in the composer before anything is sent, because an operator who cannot see what
    /// will be said cannot be responsible for it — and the node marks this as the operator's, so
    /// the agent reads it as the principal speaking.
    /// </remarks>
    public string Compose() =>
        $"About conversation '{Id}'.{Environment.NewLine}{Environment.NewLine}{Says}";

    /// <summary>Puts one instruction's outcome into words.</summary>
    /// <param name="result">What the broker reported.</param>
    /// <returns>A sentence.</returns>
    /// <remarks>
    /// The last arm was <c>"Still pending."</c> and nothing could reach it: the broker turned its
    /// own impatience into <c>failed</c>, which told the operator the turn had not completed while
    /// the agent was still working — and settled the record, so the real answer was refused when it
    /// arrived. It is now the ordinary outcome for any turn longer than a short wait, and says
    /// where the answer will be rather than implying something went wrong.
    /// </remarks>
    private static string Describe(InstructionResult result) => result.Outcome switch
    {
        InstructionOutcome.Taken => $"Taken. {result.Reply}",
        InstructionOutcome.Declined => $"Declined, which is its right. {result.Reply}",
        InstructionOutcome.Undeliverable =>
            InstructionWording.Undeliverable,
        InstructionOutcome.Failed => $"The turn did not complete. {result.Reply}",
        _ => "Given to the agent, and the turn is still running. Its answer will appear on that "
             + "agent's page."
    };

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

        return Page();
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

            // **What each participant is doing, from the same derivation as the terminal's room** (specs/024 FR-013):
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
