using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Web.Seating;

namespace Parley.Web.Pages;

/// <summary>
/// Leaving: releasing every seat, and forgetting the name.
/// </summary>
/// <remarks>
/// <para>
/// A POST and never a GET, like every other operation in this console that changes something.
/// Leaving drops an attachment, which SPEC.md §4.0 makes an agent's departure — so a link that
/// did it could be followed by anything that crawls a page and would take somebody out of the
/// deployment without their touching it.
/// </para>
/// <para>
/// It releases the seats first and clears the cookie second. The other order leaves a console
/// that has forgotten whose seats these are while still holding them, and nothing left anywhere
/// able to name them for release short of the idle sweep.
/// </para>
/// </remarks>
/// <param name="seats">The seats this console is holding.</param>
public sealed class LeaveModel(SeatBook seats) : PageModel
{
    /// <summary>Who left.</summary>
    public string? Person { get; private set; }

    /// <summary>How many seats were given up.</summary>
    public int Released { get; private set; }

    /// <summary>Whether this render is the departure rather than the offer of one.</summary>
    /// <remarks>
    /// Set by the POST rather than inferred from <see cref="Released"/> being zero, because zero
    /// is a real outcome: the idle sweep releases a seat while the cookie survives, so somebody
    /// can leave for real and have nothing to give up.
    /// </remarks>
    public bool Left { get; private set; }

    /// <summary>Offers to leave, which is not the same as leaving.</summary>
    public void OnGet() => Person = SeatCookie.Read(Request)?.Person;

    /// <summary>Releases every seat and forgets the name.</summary>
    /// <returns>The page, saying what was given up.</returns>
    public IActionResult OnPost()
    {
        if (SeatCookie.Read(Request) is { } seat)
        {
            Person = seat.Person;
            Released = seats.Leave(seat);
        }

        SeatCookie.Clear(Response);

        Left = true;

        return Page();
    }
}
