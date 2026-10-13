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
/// <param name="Polls">How many closure polls.</param>
/// <param name="Problem">Why the count could not be read, when it could not.</param>
/// <param name="Delivered">What this person owes an answer to in the project: each agent's latest unanswered request or question.</param>
public sealed record Pending(
    string Project,
    int Waiting,
    int Polls,
    string? Problem,
    IReadOnlyList<Delivered> Delivered);

/// <summary>
/// A person's own inbox: what participants have sent them, and the Results and replies they owe back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Taking delivery is an act, and this page is built around saying so.</b> SPEC.md §6 separates work
/// nobody collected from work collected and left, and records collection by the inbox drain. A page that
/// drained while rendering would mark work picked up for somebody who had merely clicked a link.
/// </para>
/// <para>
/// Sending Results is here for a reason that is not symmetry: an inbox that could receive a Request and not
/// report on it would leave an exchange open until a person closed the whole conversation.
/// </para>
/// </remarks>
/// <param name="directory">This machine's projects.</param>
/// <param name="seats">The seats this console is holding.</param>
public sealed class InboxModel(BrokerDirectory directory, SeatBook seats) : PageModel
{
    /// <summary>Who is here.</summary>
    public Seat? Me { get; private set; }

    /// <summary>What is waiting in each project this person is seated in.</summary>
    public IReadOnlyList<Pending> Projects { get; private set; } = [];

    /// <summary>How much is waiting for this name while no seat is held, and whether every project answered.</summary>
    /// <remarks>
    /// Only read when <see cref="Projects"/> is empty, which is the state this page used to describe as "you are
    /// seated nowhere, so nothing can be waiting". That sentence read the absence of a collector as the absence of
    /// mail, and the owner met the case that proves them different: two Results waiting for a name whose seat a
    /// broker restart had taken. Counted without opening an attachment, so reading the page still does not make
    /// anybody present.
    /// </remarks>
    public (int Waiting, bool Certain) Away { get; private set; }

    /// <summary>What became of sending, or of clearing.</summary>
    public string? Outcome { get; private set; }

    /// <summary>The project a form was submitted for.</summary>
    [BindProperty]
    public string? For { get; set; }

    /// <summary>The exchange being reported on or clarified.</summary>
    [BindProperty]
    public long Exchange { get; set; }

    /// <summary>The item identifiers being reported on, in order.</summary>
    [BindProperty]
    public IList<string> Ids { get; set; } = [];

    /// <summary>What is being said about each, in the same order.</summary>
    [BindProperty]
    public IList<string> Texts { get; set; } = [];

    /// <summary>The outcome chosen for each, in the same order.</summary>
    [BindProperty]
    public IList<string> Verdicts { get; set; } = [];

    /// <summary>A clarification, or the reply to one.</summary>
    [BindProperty]
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
    /// It redirected to a Join page when nobody had typed a name. Now the seats are taken on every render
    /// (<see cref="PersonSeats"/>), so there is always somebody this page is about.
    /// </remarks>
    public async Task<IActionResult> OnGetAsync()
    {
        await PersonSeats.EnsureAsync(seats, directory, HttpContext.RequestAborted).ConfigureAwait(false);

        Me = seats.Seats().FirstOrDefault()?.Seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Sends the Result of one exchange.</summary>
    /// <returns>The page, saying what happened.</returns>
    public async Task<IActionResult> OnPostResultAsync()
    {
        var (seat, hold, voice) = Seated();

        if (seat is null || hold is null || voice is null)
        {
            // Back to this page, which takes the seat again or says why it cannot (there is no Join).
            return RedirectToPage("/Inbox");
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

        var sent = await voice
            .ResultAsync(hold, Exchange, outcomes, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        Outcome = sent.Says;

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
        var (seat, hold, voice) = Seated();

        if (seat is null || hold is null || voice is null)
        {
            // Back to this page, which takes the seat again or says why it cannot (there is no Join).
            return RedirectToPage("/Inbox");
        }

        var sent = await voice
            .ClarifyAsync(hold, Exchange, Clarification, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        Outcome = sent.Says;

        Me = seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>The seat this person holds in the project a form named.</summary>
    /// <returns>The seat, the hold and a voice, or nulls when there is none.</returns>
    private (Seat? Seat, SeatHold? Hold, SeatVoice? Voice) Seated()
    {
        if (directory.Find(For) is not { } project || seats.Held(project.Name.Value) is not { } hold)
        {
            return (null, null, null);
        }

        return (hold.Seat, hold, new SeatVoice(directory.AgentsFor(project), hold.Seat));
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

            // **Showing a message is delivering it** (the owner's call of 2026-10-13, as the terminal has done since
            // 2026-10-08). It was a button, which a person met as a count that never went down, and reading a Request
            // they had been sent did not count as having picked it up. Everything waiting is taken, because the broker's
            // inbox has no narrower drain, and it is taken here because this is where it is drawn.
            if (problem is null && (messages > 0 || polls > 0))
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

            pending.Add(new Pending(hold.Project, messages, polls, problem, owed));
        }

        // What is owed does not depend on this console holding the seat: it is the broker's wait-for graph and the person's
        // name. A project this console is not seated in still shows what is waiting on the person, as long as anything is.
        foreach (var project in directory.Projects.Where(p => seats.Held(p.Name.Value) is null))
        {
            var read = await BrokerDirectory
                .ReadAsync(async () => await OwedAsync(project, PersonSeat.On(project.Name), token).ConfigureAwait(false))
                .ConfigureAwait(false);

            if (read.Problem is null && read.Value is { Count: > 0 } owedHere)
            {
                pending.Add(new Pending(project.Name.Value, 0, 0, null, owedHere));
            }
        }

        Projects = pending;

        if (pending.Count == 0)
        {
            // Counted from the seats this console knows whether or not any is held: mail waits against the name.
            Away = await seats.WaitingAsync(directory.Projects, token).ConfigureAwait(false);
        }
    }

    /// <summary>What a project's agents are waiting on this person for, each as the agent's latest message in that exchange.</summary>
    /// <param name="project">The project.</param>
    /// <param name="me">The person's seat.</param>
    /// <param name="token">Cancels the reads.</param>
    /// <returns>The messages, oldest first, in the shape the page draws a message in.</returns>
    /// <remarks>
    /// <para>
    /// The wait-for graph says which exchanges an agent is waiting on the person for, and only the conversation says what was
    /// asked, so the conversations the person is party to that have an open exchange are read and
    /// <see cref="WaitingOnYou"/> picks the questions out of them, the derivation the conversation page and the terminal use.
    /// </para>
    /// <para>
    /// Nothing is read when nothing waits on the person, which is the common case and costs one call.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Delivered>> OwedAsync(
        ProjectSettings project, AgentName me, CancellationToken token)
    {
        var broker = new BrokerClient(directory.ClientFor(project));

        var waiting = await broker.WaitingAsync(token).ConfigureAwait(false);
        var onMe = waiting.Where(edge => edge.WaitedOn == me && edge.Waiter != me).ToList();

        if (onMe.Count == 0)
        {
            return [];
        }

        var open = new List<string>();
        string? next = null;

        do
        {
            var page = await broker
                .ConversationsAsync(me, pageToken: next, includeArchived: true, cancellationToken: token)
                .ConfigureAwait(false);

            open.AddRange(page.Items.Where(c => c.HasOpenExchange).Select(c => c.Id));
            next = page.NextPageToken;
        }
        while (next is { Length: > 0 });

        var exports = await Task.WhenAll(
            open.Select(id => broker.ExportAsync(id, token))).ConfigureAwait(false);

        return
        [
            .. exports
                .SelectMany(export => WaitingOnYou.From(export, onMe, me)
                    .SelectMany(on => on.Questions)
                    .Where(question => question.At is not null)
                    .Select(question => export.Messages.FirstOrDefault(m => m.Id == question.At))
                    .OfType<Message>())
                .OrderBy(message => message.CreatedUtc)
                .Select(message => new Delivered(message)),
        ];
    }
}
