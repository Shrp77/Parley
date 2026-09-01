using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Web.Rendering;
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

    /// <summary>Why this project could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>When the broker answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>Who the operator is writing to.</summary>
    [BindProperty]
    public IList<string> Targets { get; set; } = [];

    /// <summary>What the operator wrote.</summary>
    [BindProperty]
    public string? Says { get; set; }

    /// <summary>Whether an answer is wanted back.</summary>
    /// <remarks>
    /// It means two different things depending on who is writing, which is the honest reading of
    /// both. From a seat it chooses between §10.1's <c>ask</c> and <c>notify</c>, and an
    /// exchange is opened or not. From the operator it is a sentence in an instruction, because
    /// §11.5 has no reply channel and the agent may decline the whole thing.
    /// </remarks>
    [BindProperty]
    public bool WantsAnswer { get; set; }

    /// <summary>What the person is trying to decide, when they are asking a question.</summary>
    /// <remarks>
    /// Only reached from a seat. §8 rule 1 is enforced in the schema, so a question without it
    /// is refused by a <c>CHECK</c> constraint — this asks for it in the form instead, where the
    /// answer is a sentence rather than a debugging session.
    /// </remarks>
    [BindProperty]
    public string? Intent { get; set; }

    /// <summary>What turns on the answer, when they are asking a question.</summary>
    [BindProperty]
    public string? Consequences { get; set; }

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
    /// <returns>The state in words, or empty when the message belongs to no exchange.</returns>
    /// <remarks>
    /// Read from the export rather than inferred from the messages, because an orphaned exchange
    /// and an open one carry exactly the same traffic — and an operator opens a conversation
    /// precisely because something went wrong.
    /// </remarks>
    public string StateOf(Message message) =>
        message.ExchangeId is { } id && Exchanges.TryGetValue(id, out var snapshot)
            ? snapshot.State switch
            {
                ExchangeState.Open => "still open, awaiting an answer",
                ExchangeState.Orphaned => "orphaned: nobody returned to it",
                _ => "closed"
            }
            : string.Empty;

    /// <summary>The identifier of the newest message on this page.</summary>
    /// <remarks>
    /// What a staleness check compares against. A transcript must not change under its reader, so
    /// the page says it has fallen behind rather than growing while somebody is partway down it.
    /// </remarks>
    public long Newest => Messages.Count == 0 ? 0 : Messages[^1].Id;

    /// <summary>Says whether the conversation has gained messages since a page was rendered.</summary>
    /// <param name="since">The newest message the reader has.</param>
    /// <returns>How many have arrived since.</returns>
    public async Task<IActionResult> OnGetSinceAsync(long since)
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return new JsonResult(new { arrived = 0 });
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ExportAsync(Id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (read.Value is not { } export)
        {
            // Not zero. A broker that stopped answering and a conversation that gained nothing
            // are the same number, and reporting the first as the second leaves a transcript
            // quietly asserting it is current for as long as the tab stays open — which is the
            // failure this whole check exists to prevent, arriving by the back door.
            return new JsonResult(new { arrived = 0, checkFailed = true });
        }

        return new JsonResult(
            new { arrived = export.Messages.Count(m => m.Id > since), checkFailed = false });
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
    /// participants from being party to an exchange, and there is no operator <c>ask</c> or
    /// <c>notify</c> — an operator is not an agent and has no name in the registry to send as.
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

        // Which voice this is, decided once rather than per recipient. A composer that spoke as
        // the person to one agent and as the operator to another would put two different kinds of
        // thing in one row of outcomes, and only one of them lands in the transcript.
        var here = SeatCookie.Read(Request);
        var seated = here is null ? null : seats.Held(project.Name.Value, here);

        // A seat that lapsed between rendering this page and posting it. The composer said "you
        // are in this conversation as X"; falling through to the operator's voice would send an
        // instruction instead, report a different kind of outcome, and put nothing in the
        // transcript — having told somebody the opposite a moment earlier.
        if (here is not null && seated is null)
        {
            SendProblem =
                $"Your seat in {project.Name.Value} is no longer held, so nothing was sent. "
                + "Join again and it will be taken back.";

            await OnGetAsync().ConfigureAwait(false);

            return Page();
        }

        var voice = seated is null
            ? null
            : new SeatVoice(directory.AgentsFor(project), seated.Seat);

        var broker = seated is null ? new BrokerClient(directory.ClientFor(project)) : null;

        foreach (var target in Targets)
        {
            if (!AgentName.TryCreate(target, out var agent))
            {
                sent.Add((target, "That is not a name an agent can have."));
                continue;
            }

            if (voice is not null)
            {
                sent.Add((target, WantsAnswer
                    ? await voice.AskAsync(agent, Intent, Says, Consequences, Id, token)
                        .ConfigureAwait(false)
                    : await voice.NotifyAsync(agent, Says, Id, token).ConfigureAwait(false)));

                continue;
            }

            var read = await BrokerDirectory.ReadAsync(async () =>
                await broker!.InstructAsync(agent, Compose(), token).ConfigureAwait(false))
                .ConfigureAwait(false);

            sent.Add((target, read.Value is { } result ? Describe(result) : read.Problem!));
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
    public string Compose()
    {
        var opening = WantsAnswer ? "I would like a reply." : "No reply needed.";

        return $"About conversation '{Id}'. {opening}{Environment.NewLine}{Environment.NewLine}{Says}";
    }

    /// <summary>Puts one instruction's outcome into words.</summary>
    /// <param name="result">What the broker reported.</param>
    /// <returns>A sentence.</returns>
    private static string Describe(InstructionResult result) => result.Outcome switch
    {
        InstructionOutcome.Taken => $"Taken. {result.Reply}",
        InstructionOutcome.Declined => $"Declined, which is its right. {result.Reply}",
        InstructionOutcome.Undeliverable =>
            "Not delivered: nothing is running for that agent to receive it.",
        InstructionOutcome.Failed => $"The turn did not complete. {result.Reply}",
        _ => "Still pending."
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

        Seat = SeatCookie.Read(Request) is { } who ? seats.Held(project.Name.Value, who) : null;

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ExportAsync(Id, HttpContext.RequestAborted).ConfigureAwait(false))
            .ConfigureAwait(false);

        Messages = read.Value?.Messages ?? [];
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
        }
        AsOf = DateTimeOffset.Now;

        return Page();
    }
}
