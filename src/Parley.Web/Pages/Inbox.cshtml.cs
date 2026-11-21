using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Client;
using Parley.Web.Seating;

namespace Parley.Web.Pages;

/// <summary>What is waiting for a person, and what they owe an answer to.</summary>
/// <param name="Project">The project it is waiting in.</param>
/// <param name="Waiting">How many messages, as of this page.</param>
/// <param name="Problem">Why the count could not be read, when it could not.</param>
/// <param name="Delivered">What this person owes an answer to in the project: each agent's latest unanswered request or question.</param>
public sealed record Pending(
    string Project,
    int Waiting,
    string? Problem,
    IReadOnlyList<Delivered> Delivered);

/// <summary>
/// A person's own inbox: what participants have sent them, and the Results and replies they owe back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Showing a message is delivering it.</b> The page counts what is waiting for each seat this console
/// holds and then drains it while rendering, as the terminal does, because the broker's inbox has no
/// narrower drain and this is where the messages are drawn. SPEC.md §6 records collection by the drain, so
/// opening Waiting on you marks the person's waiting mail delivered; In flight and Closed take nothing (§5.6). A seat that is not held is only counted,
/// without draining, so reading the page then makes nobody present.
/// </para>
/// <para>
/// Sending Results is here for a reason that is not symmetry: an inbox that could receive a Request and not
/// report on it would leave an exchange open until a person closed the whole conversation.
/// </para>
/// </remarks>
/// <param name="directory">This machine's projects.</param>
/// <param name="seats">The seats this console is holding.</param>
/// <param name="notices">What the console tells the person, which the bell lists.</param>
/// <param name="stillOpen">What the page last asked about which questions are still open.</param>
[RequestSizeLimit(Parley.Web.Pages.Projects.FilesModel.PlaceLimitBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = Parley.Web.Pages.Projects.FilesModel.PlaceLimitBytes)]
public sealed class InboxModel(
    BrokerDirectory directory,
    SeatBook seats,
    Parley.Web.Notifications.NotificationLog notices,
    StillOpenAsks stillOpen) : PageModel
{
    /// <summary>Takes the person's seats back before any post acts on them.</summary>
    /// <param name="context">The handler about to run.</param>
    /// <param name="next">Runs it.</param>
    /// <returns>When the handler has run.</returns>
    /// <remarks>
    /// The book lets every seat go after twenty minutes with no render, and the frame takes them back only when it draws,
    /// after the handler. A follow-up written over a long lunch then found no seat and was dropped without a word, and
    /// asking which are still open asked nobody. A post is a person at the console, so it takes the seats first, as the
    /// conversation page does before it sends.
    /// </remarks>
    public override async Task OnPageHandlerExecutionAsync(
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (HttpMethods.IsPost(Request.Method))
        {
            await PersonSeats.EnsureAsync(seats, directory, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>Each agent last asked which of its questions are still open, and where its answer stands now.</summary>
    public IReadOnlyList<StillOpenStanding> Asked { get; private set; } = [];

    /// <summary>Who is here.</summary>
    public Seat? Me { get; private set; }

    /// <summary>What is waiting in each project this person is seated in.</summary>
    public IReadOnlyList<Pending> Projects { get; private set; } = [];

    private Dictionary<string, IReadOnlyList<string>> mentionable = new(StringComparer.Ordinal);

    /// <summary>The agents typing <c>@</c> offers in a box answering something in a project.</summary>
    /// <param name="project">The project's name.</param>
    /// <returns>Their full names, or none when the project's registry could not be read.</returns>
    public IReadOnlyList<string> MentionsIn(string project) =>
        mentionable.TryGetValue(project, out var names) ? names : [];

    /// <summary>How much is waiting for this name while no seat is held, and whether every project answered.</summary>
    /// <remarks>
    /// Only read when <see cref="Projects"/> is empty. The absence of a collector is not the absence of mail, which
    /// waits against the name. Counted without opening an attachment, so reading the page does not make
    /// anybody present.
    /// </remarks>
    public (int Waiting, bool Certain) Away { get; private set; }

    /// <summary>The projects whose mail this page could not deliver, each with why, so the count in the frame is explained.</summary>
    public IReadOnlyList<string> NotDelivered { get; private set; } = [];

    /// <summary>What became of sending, or of clearing.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Whether a send was just refused, so what the person wrote goes back in its boxes.</summary>
    private bool _refused;

    /// <summary>Which form's files were kept when a send was refused: <c>Result</c> or <c>Clarify</c>.</summary>
    /// <remarks>Both forms of a row answer one exchange, so the exchange alone would put the files back in both.</remarks>
    private string? _filesFor;

    /// <summary>What the person pasted or dropped into a message box, to place and name in the message.</summary>
    [BindProperty, FromForm]
    public IList<IFormFile> Attachments { get; set; } = [];

    /// <summary>Files an earlier press placed before its send failed, each as its name and hash, so they are not uploaded again.</summary>
    /// <remarks>Only ever words in a message the person is sending: nothing is opened or fetched from these.</remarks>
    [BindProperty, FromForm]
    public IList<string> Attached { get; set; } = [];

    /// <summary>The files a refused send kept for one form of a row, to go back in its box.</summary>
    /// <param name="exchange">The row's exchange.</param>
    /// <param name="form"><c>Result</c> or <c>Clarify</c>.</param>
    /// <returns>The kept files, or nothing.</returns>
    public IReadOnlyList<string>? KeptFiles(long exchange, string form) =>
        Kept(exchange) && string.Equals(_filesFor, form, StringComparison.Ordinal) ? [.. Attached] : null;

    /// <summary>Whether this exchange's form was just refused, and holds what the person wrote.</summary>
    /// <param name="exchange">The exchange.</param>
    /// <returns>Whether its row is opened and its boxes filled again.</returns>
    /// <remarks>Refused or not, a page that came back with empty boxes would lose everything they typed.</remarks>
    public bool Kept(long exchange) => _refused && exchange == Exchange;

    /// <summary>What the person wrote in an item's box, when the send was refused.</summary>
    /// <param name="exchange">The exchange.</param>
    /// <param name="id">The item.</param>
    /// <returns>Their words, or nothing.</returns>
    public string? KeptText(long exchange, string id) =>
        Kept(exchange) && Ids.IndexOf(id) is >= 0 and var at && at < Texts.Count ? Texts[at] : null;

    /// <summary>The outcome the person chose for an item, when the send was refused.</summary>
    /// <param name="exchange">The exchange.</param>
    /// <param name="id">The item.</param>
    /// <returns>Their choice, or nothing.</returns>
    public string? KeptVerdict(long exchange, string id) =>
        Kept(exchange) && Ids.IndexOf(id) is >= 0 and var at && at < Verdicts.Count ? Verdicts[at] : null;

    /// <summary>The project a form was submitted for.</summary>
    [BindProperty, FromForm]
    public string? For { get; set; }

    /// <summary>The exchange being reported on or clarified.</summary>
    [BindProperty, FromForm]
    public long Exchange { get; set; }

    /// <summary>The item identifiers being reported on, in order.</summary>
    [BindProperty, FromForm]
    public IList<string> Ids { get; set; } = [];

    /// <summary>What is being said about each, in the same order.</summary>
    [BindProperty, FromForm]
    public IList<string> Texts { get; set; } = [];

    /// <summary>The outcome chosen for each, in the same order.</summary>
    [BindProperty, FromForm]
    public IList<string> Verdicts { get; set; } = [];

    /// <summary>A clarification, or the reply to one.</summary>
    [BindProperty, FromForm]
    public string? Clarification { get; set; }

    /// <summary>Text a message must contain (who sent it, its conversation or its words); empty for every message.</summary>
    /// <remarks>
    /// The filters are a form that asks for the page again, so they work with no script and survive its reload. They narrow
    /// what is drawn and nothing else: what is waiting, and what taking delivery takes, is not changed by them.
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>The one project to show, or empty for all.</summary>
    /// <remarks>Not called <c>project</c>, which the frame reads as "the page is inside that project".</remarks>
    [BindProperty(SupportsGet = true)]
    public string? Scope { get; set; }

    /// <summary>Which view to show: empty for Waiting on you, <c>flight</c> or <c>closed</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? View { get; set; }

    /// <summary>Which closed window: 0 for the latest, 1 for the one before it, and so on.</summary>
    [BindProperty(SupportsGet = true)]
    public int Older { get; set; }

    /// <summary>The agent a follow-up goes to.</summary>
    [BindProperty, FromForm]
    public string? Agent { get; set; }

    /// <summary>The conversation a follow-up goes in, and where the files a message names are placed.</summary>
    [BindProperty, FromForm]
    public string? Conversation { get; set; }

    /// <summary>What a follow-up says.</summary>
    [BindProperty, FromForm]
    public string? Words { get; set; }

    /// <summary>The view being shown.</summary>
    public InboxView Showing => View switch
    {
        "flight" => InboxView.InFlight,
        "closed" => InboxView.Closed,
        _ => InboxView.WaitingOnYou,
    };

    /// <summary>The three views in each project, or why a project could not be read.</summary>
    public IReadOnlyList<(string Project, InboxReading? Reading, string? Problem)> Readings { get; private set; } = [];

    /// <summary>How many days the Closed view covers, from this machine's settings.</summary>
    public int ClosedDays { get; private set; } = Inbox.DefaultDays;

    /// <summary>How many rows a view has across every project the filters keep.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The count.</returns>
    public int CountOf(InboxView view) =>
        view == InboxView.WaitingOnYou
            ? Projects.SelectMany(p => p.Delivered.Select(item => (p.Project, item))).Count(row => Keep(row.Project, row.item))
            : RowsOf(view).Count;

    /// <summary>The rows of In flight or Closed across every project, newest first, as the filters keep them.</summary>
    /// <param name="view">The view.</param>
    /// <returns>Each row with its project.</returns>
    public IReadOnlyList<(string Project, InboxRow Row)> RowsOf(InboxView view) =>
        [.. Readings
            .Where(r => r.Reading is not null)
            .SelectMany(r => r.Reading!.In(view).Select(row => (r.Project, Row: row)))
            .Where(pair => Keep(pair.Project, pair.Row))
            .OrderByDescending(pair => pair.Row.MovedUtc)];

    /// <summary>Whether this row's follow-up was just refused, so its words go back in the box.</summary>
    /// <param name="project">The row's project.</param>
    /// <param name="exchange">The row's exchange.</param>
    /// <returns>Whether to fill the box again.</returns>
    public bool FollowUpKept(string project, long exchange) =>
        _followUpRefused && exchange == Exchange && string.Equals(project, For, StringComparison.Ordinal);

    private bool _followUpRefused;

    /// <summary>Whether a row of In flight or Closed passes the filters.</summary>
    /// <param name="project">Its project.</param>
    /// <param name="row">The row.</param>
    /// <returns><see langword="true"/> to draw it.</returns>
    public bool Keep(string project, InboxRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!string.IsNullOrEmpty(Scope) && Scope != project)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Q))
        {
            return true;
        }

        var needle = Q.Trim();
        var payload = row.Last is { } last ? Payload.Read(last.PayloadJson) : null;

        return row.Agent.Value.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || row.Conversation.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || (payload?.Said?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
               || (payload?.Points.Any(point => point.Text?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ?? false);
    }

    /// <summary>Sends a follow-up: a new Request to a row's agent in the row's conversation (SPEC.md §5.6).</summary>
    /// <returns>The page, saying what happened.</returns>
    /// <remarks>
    /// A person's line to an agent is a new Request, so the row's own exchange is never reopened or added to. Not offered to
    /// the facilitator, which is never given work, and refused here too should a form ask for it.
    /// </remarks>
    public async Task<IActionResult> OnPostFollowUpAsync()
    {
        var (_, hold, _, why) = await SeatedAsync().ConfigureAwait(false);
        var settings = For is { Length: > 0 } named ? directory.Find(named) : null;

        if (hold is null || settings is null)
        {
            return await NotSeatedAsync(followUp: true, "sent", why ?? "This console has no such project.").ConfigureAwait(false);
        }

        if (!AgentName.TryCreate(Agent, out var agent) || Facilitation.Is(agent) || string.IsNullOrWhiteSpace(Conversation))
        {
            _followUpRefused = true;
            Outcome = "A follow-up goes to one agent, in the conversation its row is in.";
        }
        else if (string.IsNullOrWhiteSpace(Words) && Attachments.Count == 0 && Attached.Count == 0)
        {
            _followUpRefused = true;
            Outcome = "Nothing was written, so nothing was sent.";
        }
        else if (await PlaceFilesAsync(settings).ConfigureAwait(false) is { } placeProblem)
        {
            _followUpRefused = true;
            Outcome = placeProblem;
        }
        else
        {
            var voice = new SeatVoice(directory.AgentsFor(settings), hold.Seat);
            var sent = await voice
                .RequestOpeningAsync(agent, Attaching.Message(Words, Attached), Conversation, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            _followUpRefused = !sent.Sent;
            Outcome = sent.Says;
            notices.Add(For ?? string.Empty, Conversation, "Inbox", sent.Says, sent.Sent);
        }

        Me = hold.Seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Asks the agent a row is waiting on to look at what is waiting for it now.</summary>
    /// <returns>The page, saying what was asked.</returns>
    /// <remarks>
    /// <para>
    /// The operator operation <c>start</c> (SPEC.md §10.2), which is what a person's Start is: a node that can resolve the
    /// agent starts it, or, when it is already running as a process the node started, gives it a turn. Either way the agent
    /// collects its mail, which is how it reads the message. It costs the agent a turn and nothing else: no message is written,
    /// and the exchange is not touched. One click, because nothing is lost by it.
    /// </para>
    /// <para>
    /// Not offered for the facilitator, which no node runs, or for the person's seat, and refused here too should a form ask. The
    /// broker's own refusals (a frozen agent, one open in somebody's editor) are said in its words, since each says what to do.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostNudgeAsync()
    {
        var settings = For is { Length: > 0 } named ? directory.Find(named) : null;

        if (settings is null || !AgentName.TryCreate(Agent, out var agent) || Facilitation.Is(agent) || PersonSeat.Is(agent))
        {
            Outcome = "Only an agent can be asked to look at its messages, and this one is not an agent of a project this console has.";
        }
        else
        {
            Outcome = await NudgeAsync(new BrokerClient(directory.ClientFor(settings)), agent).ConfigureAwait(false);
        }

        Me = seats.Seats().FirstOrDefault()?.Seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    private async Task<string> NudgeAsync(BrokerClient broker, AgentName agent)
    {
        try
        {
            return await broker.StartAsync(agent, "a person asked it, from the Inbox, to look at what is waiting for it", HttpContext.RequestAborted)
                    .ConfigureAwait(false)
                ? $"'{agent.Value}' was asked to look at its messages now. Its node starts it, or gives it a turn if it is already running."
                : $"Nothing was asked of '{agent.Value}': it is retired, or no node on its machine is running.";
        }
        catch (Grpc.Core.RpcException refused) when (refused.StatusCode == Grpc.Core.StatusCode.FailedPrecondition)
        {
            // The broker says why in words a person can act on: frozen, or open in an editor Parley did not start.
            return refused.Status.Detail;
        }
        catch (Grpc.Core.RpcException failure)
        {
            return $"'{agent.Value}' was not asked: {(failure.StatusCode == Grpc.Core.StatusCode.Unavailable ? "this project's broker is not answering." : failure.Status.Detail)}";
        }
    }

    /// <summary>Whether a delivered message passes the filters.</summary>
    /// <param name="project">The project it was delivered in.</param>
    /// <param name="item">The message.</param>
    /// <returns><see langword="true"/> to draw it.</returns>
    public bool Keep(string project, Delivered item)
    {
        if (!string.IsNullOrEmpty(Scope) && Scope != project)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Q))
        {
            return true;
        }

        var needle = Q.Trim();
        var payload = Payload.Read(item.Message.PayloadJson);

        return item.Message.From.Value.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || (item.Message.ConversationId?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
               || (payload.Said?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
               || payload.Points.Any(point => point.Text?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Shows what is waiting, and whatever has already been taken.</summary>
    /// <returns>The page.</returns>
    /// <remarks>
    /// The seats are taken on every render (<see cref="PersonSeats"/>), so there is always somebody this page is about.
    /// </remarks>
    public async Task<IActionResult> OnGetAsync()
    {
        // The page's own reload is no evidence anybody is at the console (SPEC.md §14), as for the frame.
        await PersonSeats.EnsureAsync(
            seats, directory, HttpContext.RequestAborted,
            person: !Request.Query.ContainsKey(PersonSeats.Reloaded)).ConfigureAwait(false);

        Me = seats.Seats().FirstOrDefault()?.Seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>A fingerprint of what this page shows, as the page last drew it.</summary>
    public string Digest { get; private set; } = string.Empty;

    /// <summary>A fingerprint of what this page would show now, for the page to compare with what it drew.</summary>
    /// <returns>The fingerprint, as JSON.</returns>
    /// <remarks>
    /// <para>
    /// <b>So the page follows what changes without a reload that loses typing</b> (owner's report, 2026-11-06: the Inbox
    /// showed nothing new until reloaded by hand). The page asks every few seconds and reloads only when this differs, and
    /// only while nobody is writing in it; otherwise it says something changed.
    /// </para>
    /// <para>
    /// <b>A read with no effects.</b> It collects nothing, so nothing is marked delivered, and it touches no seat, so it
    /// makes nobody present. One call per project: every exchange the person waits on or is waited on for, which moves
    /// when a question arrives, is answered, is settled or is asked back, and when one of the person's requests is replied to.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnGetDigestAsync() =>
        new JsonResult(new { digest = await DigestAsync(HttpContext.RequestAborted).ConfigureAwait(false) });

    /// <summary>Reads the fingerprint of what waits on or for the person, in every project.</summary>
    /// <param name="token">Cancels the reads.</param>
    /// <returns>The fingerprint; a project that did not answer reads as a question mark, so its coming back is a change.</returns>
    private async Task<string> DigestAsync(CancellationToken token)
    {
        var parts = await Task.WhenAll(directory.Projects.Select(async project =>
        {
            var me = seats.Known(project.Name.Value)?.Name ?? PersonSeat.On(project.Name);
            var read = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project)).WaitingAsync(token).ConfigureAwait(false))
                .ConfigureAwait(false);

            var edges = read.Value is { } all
                ? string.Join(',', all
                    .Where(edge => edge.Waiter == me || edge.WaitedOn == me)
                    .Select(edge => $"{edge.ExchangeId}>{edge.WaitedOn.Value}")
                    .Order(StringComparer.Ordinal))
                : "?";

            // Where each agent the person waits on stands, so a chip that changes (a turn starting or ending) is a change too
            // (spec 036). Asked only when the person waits on somebody, which is the only time a chip is drawn.
            var standing = string.Empty;

            if (read.Value is { } graph && graph.Any(edge => edge.Waiter == me && edge.WaitedOn != me))
            {
                var awaited = graph.Where(edge => edge.Waiter == me).Select(edge => edge.WaitedOn).ToHashSet();
                var registry = await BrokerDirectory.ReadAsync(async () =>
                    await new BrokerClient(directory.ClientFor(project)).RegistryAsync(token).ConfigureAwait(false))
                    .ConfigureAwait(false);

                standing = registry.Value is { } rows
                    ? "|" + string.Join(',', rows
                        .Where(row => awaited.Contains(row.Name))
                        .Select(row => $"{row.Name.Value}={InFlightStanding.From(row, [], 0).State}")
                        .Order(StringComparer.Ordinal))
                    : "|?";
            }

            return $"{project.Name.Value}:{edges}{standing}";
        })).ConfigureAwait(false);

        return string.Join(';', parts.Order(StringComparer.Ordinal));
    }

    /// <summary>Sends the Result of one exchange.</summary>
    /// <returns>The page, saying what happened.</returns>
    public async Task<IActionResult> OnPostResultAsync()
    {
        var (seat, hold, voice, why) = await SeatedAsync().ConfigureAwait(false);

        if (seat is null || hold is null || voice is null)
        {
            return await NotSeatedAsync(followUp: false, "sent", why ?? "Your seat in that project could not be taken.").ConfigureAwait(false);
        }

        // Zipped rather than trusted to arrive in matching lengths: these are three parallel form
        // fields, and a browser that sent an uneven set would otherwise report one item with another's
        // text.
        var outcomes = Ids
            .Select((id, at) => (
                Id: id,
                Text: at < Texts.Count ? Texts[at] : string.Empty,
                Outcome: at < Verdicts.Count ? Verdicts[at] : string.Empty))
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .ToList();

        _filesFor = "Result";

        if (await PlaceFilesAsync(directory.Find(For)!).ConfigureAwait(false) is { } placeProblem)
        {
            return await RefusedAsync(seat, placeProblem).ConfigureAwait(false);
        }

        // The form sends one Result, so its files are named once, after the first item's words.
        if (outcomes.Count > 0 && Attached.Count > 0)
        {
            outcomes[0] = outcomes[0] with { Text = Attaching.Message(outcomes[0].Text, Attached) };
        }

        var sent = await voice
            .ResultAsync(hold, Exchange, outcomes, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        // Read off the flag, never the words: a Result that went says "Sent, and…", which a match on "Sent." took as refused.
        _refused = !sent.Closed;
        Outcome = sent.Says;
        notices.Add(For ?? string.Empty, null, "Inbox", sent.Says, sent.Closed);

        Me = seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Asks for a clarification on one exchange, or replies to one.</summary>
    /// <returns>The page, saying what happened.</returns>
    /// <remarks>
    /// One handler for both, because on the wire they are one message: a Clarify hands the turn to the other
    /// side, whichever side sends it.
    /// </remarks>
    public async Task<IActionResult> OnPostClarifyAsync()
    {
        var (seat, hold, voice, why) = await SeatedAsync().ConfigureAwait(false);

        if (seat is null || hold is null || voice is null)
        {
            return await NotSeatedAsync(followUp: false, "sent", why ?? "Your seat in that project could not be taken.").ConfigureAwait(false);
        }

        _filesFor = "Clarify";

        if (await PlaceFilesAsync(directory.Find(For)!).ConfigureAwait(false) is { } placeProblem)
        {
            return await RefusedAsync(seat, placeProblem).ConfigureAwait(false);
        }

        var sent = await voice
            .ClarifyAsync(hold, Exchange, Attaching.Message(Clarification, Attached), HttpContext.RequestAborted)
            .ConfigureAwait(false);

        // Read off the flag, never the words: a Result that went says "Sent, and…", which a match on "Sent." took as refused.
        _refused = !sent.Closed;
        Outcome = sent.Says;
        notices.Add(For ?? string.Empty, null, "Inbox", sent.Says, sent.Closed);

        Me = seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Closes one Request without answering it.</summary>
    /// <returns>The page, saying what happened.</returns>
    /// <remarks>The question may be stale, or answered elsewhere. The conversation stays open and the other side is told.</remarks>
    public async Task<IActionResult> OnPostCloseAsync()
    {
        var (seat, hold, _, why) = await SeatedAsync().ConfigureAwait(false);

        if (seat is null || hold is null || directory.Find(For) is not { } project)
        {
            return await NotSeatedAsync(followUp: false, "closed", why ?? "This console has no such project.").ConfigureAwait(false);
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .CloseRequestAsync(
                    Exchange,
                    string.IsNullOrWhiteSpace(Clarification) ? null : Clarification,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false)).ConfigureAwait(false);

        if (read is { Reachable: true, Value: true })
        {
            hold.Forget(Exchange);
        }

        Outcome = !read.Reachable
            ? read.Problem
            : read.Value ? "Closed, and they have been told." : "That request is not open, so nothing changed.";

        Me = seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Asks each agent whether the questions it is waiting on the person for are still open.</summary>
    /// <returns>The page, saying how many were asked and what was refused.</returns>
    /// <remarks>
    /// <para>
    /// <b>The agent closes what was answered, never this page</b> (SPEC.md §5.6). Each agent gets one Request from the
    /// person in each conversation it has questions in, listing them; its reply names the settled ones in
    /// <c>settles</c>, and the broker closes those. Its answer comes later, so this page changes nothing now.
    /// </para>
    /// <para>
    /// The round is <see cref="StillOpen.AskAsync"/>, which the terminal runs too, in every project whose seat this console
    /// holds, whatever the filters show.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostStillOpenAsync()
    {
        if (!stillOpen.TryBeginRound())
        {
            Outcome = AlreadyAsking;
            Me = seats.Seats().FirstOrDefault()?.Seat;

            await LoadAsync().ConfigureAwait(false);

            return Page();
        }

        try
        {
            return await AskWhichAreStillOpenAsync().ConfigureAwait(false);
        }
        finally
        {
            stillOpen.EndRound();
        }
    }

    /// <summary>What a press says while another round is still asking.</summary>
    internal const string AlreadyAsking = "Already asking which are still open; this press sent nothing.";

    private async Task<IActionResult> AskWhichAreStillOpenAsync()
    {
        var token = HttpContext.RequestAborted;
        var earlier = stillOpen.Latest();
        var asks = new List<StillOpenAsk>();
        var problems = new List<string>();
        var projects = new List<string>();
        var (asked, about, already) = (0, 0, 0);

        foreach (var hold in seats.Seats())
        {
            if (directory.Find(hold.Project) is not { } project)
            {
                continue;
            }

            var voice = new SeatVoice(directory.AgentsFor(project), hold.Seat);
            var read = await BrokerDirectory.ReadAsync(async () =>
                await StillOpen.AskAsync(new BrokerClient(directory.ClientFor(project)), voice, hold.Project, earlier, token)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            if (read.Value is not { } round)
            {
                problems.Add($"{hold.Project}: {read.Problem}");
                continue;
            }

            projects.Add(hold.Project);
            asks.AddRange(round.Asks);
            problems.AddRange(round.Problems);
            (asked, about, already) = (asked + round.Asked, about + round.About, already + round.Already);
        }

        stillOpen.Replace(projects, asks);
        Outcome = StillOpen.Says(new StillOpenRound(asks, asked, about, already, problems));
        notices.Add(string.Empty, null, "Inbox", Outcome, problems.Count == 0);

        Me = seats.Seats().FirstOrDefault()?.Seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Says nothing was done because the person has no seat in the form's project, keeping what they typed.</summary>
    /// <param name="followUp">Whether the post was a follow-up, whose box is kept apart from a reply's.</param>
    /// <param name="done">What would have been done: <c>sent</c> or <c>closed</c>.</param>
    /// <param name="why">Why the seat could not be taken.</param>
    /// <returns>The page.</returns>
    /// <remarks>
    /// Posts take the seats first and the handler tries once more, so this is a broker that could not seat the person, or a
    /// form naming no project here. A redirect lost the words, the view and any word of why, which is the fault round 2
    /// fixed for a refused follow-up.
    /// </remarks>
    private async Task<IActionResult> NotSeatedAsync(bool followUp, string done, string why)
    {
        // A browser cannot fill a file field again, so files that were not placed have to be chosen again, and it says so.
        Outcome = Attachments.Count > 0 ? $"Nothing was {done}. {why} Attach your files again." : $"Nothing was {done}. {why}";
        _followUpRefused = followUp;
        _refused = !followUp;
        Me = seats.Seats().FirstOrDefault()?.Seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Places the files the person attached, in the conversation the form named, before the message is sent.</summary>
    /// <param name="project">The form's project.</param>
    /// <returns>Why nothing may be sent, or nothing when every file is placed or there were none.</returns>
    private async Task<string?> PlaceFilesAsync(ProjectSettings project)
    {
        if (Attachments.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(Conversation))
        {
            return "Nothing was sent. The files had no conversation to go in, so attach them again.";
        }

        var placing = await ComposerFiles
            .PlaceAsync(new BrokerClient(directory.ClientFor(project)), Conversation, Attachments, Attached, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return placing.Problem
               ?? (placing.ConversationNotStarted ? "Nothing was sent. The conversation could not be found, so attach the files again." : null);
    }

    /// <summary>Says a reply or Result was not sent, keeping its words and the files already placed.</summary>
    /// <param name="seat">The person's seat.</param>
    /// <param name="problem">Why.</param>
    /// <returns>The page.</returns>
    private async Task<IActionResult> RefusedAsync(Seat seat, string problem)
    {
        _refused = true;
        Outcome = problem;
        Me = seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>The seat this person holds in the project a form named, taken back first when it was let go.</summary>
    /// <returns>The seat, the hold and a voice, or nulls and why there is none.</returns>
    /// <remarks>
    /// <b>A press of Send is the person asking for it to go</b> (owner, 2026-11-11). The seats are taken before the handler
    /// runs; when that failed and taking it again here works, the words go now rather than coming back with "press it
    /// again". One more try, not a loop: a broker that refuses twice is said so, with the words kept.
    /// </remarks>
    private async Task<(Seat? Seat, SeatHold? Hold, SeatVoice? Voice, string? Problem)> SeatedAsync()
    {
        if (For is not { Length: > 0 } || directory.Find(For) is not { } project)
        {
            return (null, null, null, "This console has no such project.");
        }

        var hold = seats.Held(project.Name.Value);

        if (hold is null)
        {
            var again = await PersonSeats.EnsureAsync(seats, directory, project, HttpContext.RequestAborted).ConfigureAwait(false);

            if (again.Hold is not { } taken)
            {
                return (null, null, null, again.Problem ?? "Your seat in that project could not be taken.");
            }

            hold = taken;
        }

        return (hold.Seat, hold, new SeatVoice(directory.AgentsFor(project), hold.Seat), null);
    }

    /// <summary>Reads what is waiting in every project this person is seated in.</summary>
    /// <returns>A task completing when every project has answered or failed.</returns>
    /// <remarks>
    /// The count comes from the broker per render rather than from the notice the attach stream pushed. A
    /// notice is superseded rather than queued, so a console that had been quiet would show whatever number
    /// happened to arrive last — and this page is the one place the number is the whole point.
    /// </remarks>
    private async Task LoadAsync()
    {
        var pending = new List<Pending>();
        var token = HttpContext.RequestAborted;
        var unseated = new List<string>();

        // Read before what is drawn, so a change that lands while the page is read is a difference the next ask finds: at
        // worst one reload too many, never a change missed.
        Digest = await DigestAsync(token).ConfigureAwait(false);

        foreach (var hold in seats.Seats())
        {
            var project = directory.Find(hold.Project);

            if (project is null)
            {
                continue;
            }

            var voice = new SeatVoice(directory.AgentsFor(project), hold.Seat);

            var counts = await BrokerDirectory
                .ReadAsync(async () => await voice.WaitingAsync(token).ConfigureAwait(false))
                .ConfigureAwait(false);

            var (messages, polls, problem) = (counts.Value.Messages, counts.Value.ClosurePolls, counts.Problem);

            // **Showing a message is delivering it**, and only Waiting on you shows what waits (SPEC.md §5.6, owner
            // 2026-11-09): In flight and Closed collect nothing. Everything waiting is taken, because the broker's inbox
            // has no narrower drain, and it is taken here because this is where it is drawn.
            if (problem is null && Showing == InboxView.WaitingOnYou && (messages > 0 || polls > 0))
            {
                var taken = await BrokerDirectory
                    .ReadAsync(async () => await voice.TakeDeliveryAsync(hold, token).ConfigureAwait(false))
                    .ConfigureAwait(false);

                if (taken.Problem is null)
                {
                    (messages, polls) = (0, 0);
                }
                else
                {
                    problem = taken.Problem;
                }
            }

            // What the person owes is read from the broker, never from what this console happens to have shown: the
            // console's memory is gone at a restart, the debt is not.
            IReadOnlyList<Delivered> owed = [];

            if (problem is null)
            {
                var read = await BrokerDirectory
                    .ReadAsync(async () => await OwedAsync(project, hold.Seat.Name, token).ConfigureAwait(false))
                    .ConfigureAwait(false);

                if (read.Problem is null)
                {
                    owed = read.Value ?? [];
                }
                else
                {
                    problem = read.Problem;
                }
            }

            pending.Add(new Pending(hold.Project, messages, problem, owed));
        }

        // What is owed does not depend on this console holding the seat: it is the broker's wait-for graph and the person's
        // name. A project this console is not seated in still shows what is waiting on the person, as long as anything is.
        foreach (var project in directory.Projects.Where(p => seats.Held(p.Name.Value) is null))
        {
            var read = await BrokerDirectory
                .ReadAsync(async () => await OwedAsync(project, PersonSeat.On(project.Name), token).ConfigureAwait(false))
                .ConfigureAwait(false);

            // Said when the seat cannot be taken: mail waits against the name, so a project whose registry cannot be
            // read holds messages this page can neither deliver nor list, and the count in the frame would otherwise
            // disagree with the rows with no word as to why.
            var outcome = await PersonSeats.EnsureAsync(seats, directory, project, token).ConfigureAwait(false);
            var cannotSeat = outcome.Seated ? null : $"Its messages cannot be delivered here: {outcome.Problem ?? "the person's seat could not be taken."}";

            if (cannotSeat is not null)
            {
                unseated.Add($"{project.Name.Value}: {cannotSeat}");
            }

            if (read.Problem is null && read.Value is { Count: > 0 } owedHere)
            {
                pending.Add(new Pending(project.Name.Value, 0, null, owedHere));
            }
        }

        Projects = pending;
        NotDelivered = unseated;

        // The three views (spec 036), read for every project whatever is shown, so each view's count is right on every page.
        // Read only: no delivery is taken here.
        ClosedDays = directory.ManifestPath is { Length: > 0 } root
            ? Parley.Configuration.ParleySettings.ReadClosedDays(root)
            : Inbox.DefaultDays;

        var now = DateTimeOffset.UtcNow;

        Readings = await Task.WhenAll(directory.Projects.Select(async project =>
        {
            var me = seats.Known(project.Name.Value)?.Name ?? PersonSeat.On(project.Name);
            var read = await BrokerDirectory
                .ReadAsync(async () => await Inbox
                    .ReadAsync(new BrokerClient(directory.ClientFor(project)), me, ClosedDays, Older, now, token)
                    .ConfigureAwait(false))
                .ConfigureAwait(false);

            return (project.Name.Value, read.Value, read.Problem);
        })).ConfigureAwait(false);
        // Who typing @ offers in each project's boxes. A registry that cannot be read offers nobody; the box still sends.
        var rosters = await Task.WhenAll(directory.Projects.Select(async project =>
        {
            var roll = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project)).RegistryAsync(token).ConfigureAwait(false))
                .ConfigureAwait(false);

            return (Project: project.Name.Value, Names: RoomRoster.NotInIt(roll.Value, [], PersonSeat.On(project.Name)));
        })).ConfigureAwait(false);

        mentionable = rosters.ToDictionary(
            roster => roster.Project,
            roster => (IReadOnlyList<string>)[.. roster.Names.Select(name => name.Value)],
            StringComparer.Ordinal);
        Asked = await StillOpen.StandingAsync(
            stillOpen.Latest(),
            name => directory.Find(name) is { } known ? new BrokerClient(directory.ClientFor(known)) : null,
            token).ConfigureAwait(false);

        if (pending.Count == 0)
        {
            // Counted from the seats this console knows whether or not any is held: mail waits against the name.
            Away = await seats.WaitingAsync(directory.Projects, token).ConfigureAwait(false);
        }
    }

    /// <summary>What a project's agents are waiting on this person for.</summary>
    /// <param name="project">The project.</param>
    /// <param name="me">The person's seat.</param>
    /// <param name="token">Cancels the reads.</param>
    /// <returns>The messages, oldest first.</returns>
    private Task<IReadOnlyList<Delivered>> OwedAsync(ProjectSettings project, AgentName me, CancellationToken token) =>
        PersonOwes.ReadAsync(new BrokerClient(directory.ClientFor(project)), me, token);
}
