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
/// A person's own inbox: what peers have sent them, and the answers they owe back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Taking delivery is an act, and this page is built around saying so.</b> SPEC.md §6 runs
/// the working window from the question being delivered and records delivery by the inbox drain,
/// so reading a question is what puts a four-hour clock on answering it — and §14 ghosts what is
/// left. A page that drained while rendering would start that clock for somebody who had merely
/// clicked a link.
/// </para>
/// <para>
/// That is the protocol rather than an awkwardness to design around. A peer whose question has
/// been read and is being ignored is in a different position from one whose question nobody ever
/// picked up, and §6 splits its two windows on exactly that difference. So the count is shown
/// freely, and the contents are taken on purpose.
/// </para>
/// <para>
/// Answering is here for a reason that is not symmetry: an inbox that could receive a question
/// and not answer it would guarantee a ghost four hours later, every time. Delivering the
/// reading half alone would have made the deployment worse, not incomplete.
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

    /// <summary>The sub-questions each owed exchange asked, by exchange.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<string>> Asked { get; private set; } =
        new Dictionary<long, IReadOnlyList<string>>();

    /// <summary>What became of taking delivery, or of answering.</summary>
    public string? Outcome { get; private set; }

    /// <summary>The project a form was submitted for.</summary>
    [BindProperty]
    public string? For { get; set; }

    /// <summary>The exchange being answered.</summary>
    [BindProperty]
    public long Exchange { get; set; }

    /// <summary>The sub-question identifiers being resolved, in order.</summary>
    [BindProperty]
    public IList<string> Ids { get; set; } = [];

    /// <summary>What is being said about each, in the same order.</summary>
    [BindProperty]
    public IList<string> Texts { get; set; } = [];

    /// <summary>The outcome chosen for each, in the same order.</summary>
    [BindProperty]
    public IList<string> Verdicts { get; set; } = [];

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
            : $"{messages.Count} taken. The working window is running on anything you now owe.";

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
            : $"{dropped} cleared. Anything you owe an answer to is still here.";

        Me = seat;

        await LoadAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Answers one exchange.</summary>
    /// <returns>The page, saying what happened.</returns>
    public async Task<IActionResult> OnPostAnswerAsync()
    {
        var (seat, hold, voice) = Seated();

        if (seat is null || hold is null || voice is null)
        {
            return RedirectToPage("/Join");
        }

        // Zipped rather than trusted to arrive in matching lengths: these are three parallel
        // form fields, and a browser that sent an uneven set would otherwise resolve one
        // sub-question with another's text.
        var resolutions = Ids
            .Select((id, at) => (
                Id: id,
                Text: at < Texts.Count ? Texts[at] : string.Empty,
                Outcome: at < Verdicts.Count ? Verdicts[at] : "unknown"))
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .ToList();

        var answered = await voice
            .AnswerAsync(hold, Exchange, resolutions, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        Outcome = answered.Says;

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
    /// The count comes from the broker per render rather than from the notice the attach stream
    /// pushed. A notice is superseded rather than queued, so a console that had been quiet would
    /// show whatever number happened to arrive last — and this page is the one place the number
    /// is the whole point.
    /// </remarks>
    private async Task LoadAsync()
    {
        var pending = new List<Pending>();
        var asked = new Dictionary<long, IReadOnlyList<string>>();
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

            foreach (var owed in hold.Delivered.Where(one => one.Owes))
            {
                asked[owed.Message.ExchangeId!.Value] = await voice
                    .SubQuestionsAsync(owed.Message.ExchangeId!.Value, token)
                    .ConfigureAwait(false);
            }
        }

        Projects = pending;
        Asked = asked;
    }
}
