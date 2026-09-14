using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client.Seating;
using Parley.Client;
using Parley.Web.Seating;

namespace Parley.Web.Pages;

/// <summary>What is waiting for a person, and what they have taken delivery of.</summary>
/// <param name="Project">The project it is waiting in.</param>
/// <param name="Waiting">How many messages, as of this page.</param>
/// <param name="Polls">How many closure polls.</param>
/// <param name="Problem">Why the count could not be read, when it could not.</param>
/// <param name="Delivered">What this person has already been handed here.</param>
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

    /// <summary>What became of taking delivery, or of sending.</summary>
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

    /// <summary>Shows what is waiting, and whatever has already been taken.</summary>
    /// <returns>The page, or the join page when nobody is seated.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        Me = SeatCookie.Read(Request);

        if (Me is null)
        {
            return RedirectToPage("/Join");
        }

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Takes delivery of everything waiting in one project.</summary>
    /// <returns>The page, now showing what was handed over.</returns>
    public async Task<IActionResult> OnPostTakeAsync()
    {
        var (seat, hold, voice) = Seated();

        if (seat is null || hold is null || voice is null)
        {
            return RedirectToPage("/Join");
        }

        var (messages, polls) = await voice
            .TakeDeliveryAsync(hold, HttpContext.RequestAborted).ConfigureAwait(false);

        Outcome = messages.Count == 0 && polls == 0
            ? "Nothing was waiting."
            : $"{messages.Count} taken. Anything you were asked to do now counts as picked up.";

        Me = seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Drops what has been read and owes nothing back.</summary>
    /// <returns>The page, without them.</returns>
    public async Task<IActionResult> OnPostClearAsync()
    {
        var (seat, hold, _) = Seated();

        if (seat is null || hold is null)
        {
            return RedirectToPage("/Join");
        }

        var dropped = hold.DismissRead();

        Outcome = dropped == 0
            ? "There was nothing to clear."
            : $"{dropped} cleared. Anything you owe a result or a reply on is still here.";

        Me = seat;

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
            return RedirectToPage("/Join");
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
            return RedirectToPage("/Join");
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
        if (SeatCookie.Read(Request) is not { } seat
            || directory.Find(For) is not { } project
            || seats.Held(project.Name.Value, seat) is not { } hold)
        {
            return (null, null, null);
        }

        return (seat, hold, new SeatVoice(directory.AgentsFor(project), seat));
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

        foreach (var hold in seats.Seats(Me!))
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

            pending.Add(new Pending(
                hold.Project,
                counts.Value.Messages,
                counts.Value.ClosurePolls,
                counts.Problem,
                hold.Delivered));
        }

        Projects = pending;
    }
}
