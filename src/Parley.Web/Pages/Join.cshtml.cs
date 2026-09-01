using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Web.Seating;

namespace Parley.Web.Pages;

/// <summary>
/// Joining the deployment as a person, and seeing where that has put you.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §11.2 has always admitted a human responder and §11.3 has always given them a name.
/// What neither could give a person sitting at a browser was somewhere to hold that identity
/// between turns, which is the one thing §11.2 says a responder above the headless tier needs.
/// This page is that: typing a name takes a seat, and the seat is an ordinary attachment — the
/// same event, in the same registry, that every other agent's arrival is.
/// </para>
/// <para>
/// It seats the person in every project this console can reach, and says so before they press
/// anything. Joining one project and not the others would leave a person present somewhere they
/// were not looking, which is the state this page exists to make legible rather than create.
/// </para>
/// </remarks>
/// <param name="directory">This machine's projects.</param>
/// <param name="seats">The seats this console is holding.</param>
public sealed class JoinModel(BrokerDirectory directory, SeatBook seats) : PageModel
{
    /// <summary>The name typed into the form.</summary>
    [BindProperty]
    public string? Person { get; set; }

    /// <summary>Where to return to, when the join was reached from somewhere in particular.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Then { get; set; }

    /// <summary>Who the console currently believes is here.</summary>
    public Seat? Current { get; private set; }

    /// <summary>The projects a join would seat somebody in.</summary>
    public IReadOnlyList<ProjectSettings> Projects => directory.Projects;

    /// <summary>Where this person is already seated.</summary>
    public IReadOnlyList<SeatHold> Held { get; private set; } = [];

    /// <summary>Why the name could not be used, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>What happened in each project, after a join was attempted.</summary>
    public IReadOnlyList<(string Project, bool Seated, string Says)> Outcomes { get; private set; }
        = [];

    /// <summary>Shows the form, and whatever seat is already held.</summary>
    public void OnGet()
    {
        Current = SeatCookie.Read(Request);
        Held = Current is null ? [] : seats.Seats(Current);
    }

    /// <summary>Takes a seat in every reachable project.</summary>
    /// <returns>The page, carrying what happened in each.</returns>
    /// <remarks>
    /// The registry is read here and handed to <see cref="SeatBook"/> rather than read there, so
    /// the check that a name is not already an agent's runs against what that project's broker
    /// actually said — and a project whose broker did not answer refuses the seat rather than
    /// taking one unchecked.
    /// </remarks>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!Seat.TryRead(Person, out var seat, out var problem))
        {
            Problem = problem;
            OnGet();

            return Page();
        }

        var token = HttpContext.RequestAborted;
        var outcomes = new List<(string, bool, string)>();

        foreach (var project in directory.Projects)
        {
            var registry = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .RegistryAsync(token).ConfigureAwait(false))
                .ConfigureAwait(false);

            var outcome = await seats
                .TakeAsync(project, seat, registry.Value, token)
                .ConfigureAwait(false);

            outcomes.Add((
                project.Name.Value,
                outcome.Seated,
                outcome.Seated
                    ? $"Seated as {seat.Name.Value}."
                    : outcome.Problem ?? "The seat was not taken."));
        }

        Outcomes = outcomes;

        if (!outcomes.Exists(one => one.Item2))
        {
            // Not written. A console that remembered the name while holding no seat anywhere
            // would show somebody as joined in its own frame and show them in nobody's roster,
            // which is the disagreement this page exists to prevent rather than produce.
            Current = null;
            Held = [];

            return Page();
        }

        SeatCookie.Write(Response, seat);

        Current = seat;
        Held = seats.Seats(seat);

        return Then is { Length: > 0 } back && Url.IsLocalUrl(back)
            ? Redirect(back)
            : Page();
    }
}
