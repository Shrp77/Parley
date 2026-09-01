namespace Parley.Web.Seating;

/// <summary>
/// Where the console remembers which person is at it.
/// </summary>
/// <remarks>
/// <para>
/// A cookie and not a credential, and the difference is worth stating plainly rather than being
/// inferred from the absence of a password field. This does not authenticate anybody. It records
/// an answer to "who are you" so the console does not have to ask again on the next page, and
/// anything able to reach the console can set it to any name.
/// </para>
/// <para>
/// That is not a gap this could close. SPEC.md §16 confines the transport to a trusted path
/// because anything able to reach a broker can claim to be any agent, and the console is a second
/// door onto that path — whoever can reach it can already retire, purge and force-conclude every
/// agent in the deployment through §10.2. A login here would be a lock on a door standing in open
/// ground.
/// </para>
/// <para>
/// A session cookie, so closing the browser forgets the name. The seat itself is released
/// separately and on its own schedule; see <see cref="SeatBook.IdleAfter"/>.
/// </para>
/// </remarks>
public static class SeatCookie
{
    /// <summary>What the cookie is called.</summary>
    public const string Key = "parley.seat";

    /// <summary>
    /// Where a write is recorded for the rest of the request that made it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting a cookie changes the <em>response</em>, and the frame reads the request — so the
    /// page that joins somebody renders with the header still offering to join, and the header
    /// only catches up on the next navigation. It was corrected on reload, which is exactly the
    /// kind of wrong that survives being looked at.
    /// </para>
    /// <para>
    /// Recorded here rather than fixed by redirecting after the post, because the join page's
    /// whole value is the per-project outcome list it renders — including the projects that
    /// refused — and a redirect throws that away to fix a header.
    /// </para>
    /// </remarks>
    private const string Written = "parley.seat.written";

    /// <summary>Reads who the console believes is here.</summary>
    /// <param name="request">The request to read.</param>
    /// <returns>The person, or <see langword="null"/> if nobody has joined.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Validated on the way out as strictly as it was on the way in, because the value arrives
    /// from a client and the name it carries is about to be written into a registry and rendered
    /// into a page. A cookie somebody edited by hand is not a name.
    /// </remarks>
    public static Seat? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // What this request did, before what it arrived with. A join and a leave both take
        // effect immediately for everything rendered after them.
        if (request.HttpContext.Items.TryGetValue(Written, out var written))
        {
            return written as Seat;
        }

        return request.Cookies.TryGetValue(Key, out var stored)
               && Seat.TryRead(stored, out var seat, out _)
            ? seat
            : null;
    }

    /// <summary>Records who is here.</summary>
    /// <param name="response">The response to set the cookie on.</param>
    /// <param name="seat">Who joined.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static void Write(HttpResponse response, Seat seat)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(seat);

        response.Cookies.Append(Key, seat.Person, Options());
        response.HttpContext.Items[Written] = seat;
    }

    /// <summary>Forgets who was here.</summary>
    /// <param name="response">The response to clear the cookie on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(Key, Options());
        response.HttpContext.Items[Written] = null;
    }

    /// <summary>How the cookie is written, in one place so the two writers cannot differ.</summary>
    /// <returns>The options.</returns>
    /// <remarks>
    /// <c>Delete</c> matches a cookie by path and the rest of its attributes, so a clear written
    /// with different options than the set leaves the original in place — a logout that reports
    /// success and changes nothing.
    /// </remarks>
    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Strict,
        Path = "/"
    };
}
