using Parley.Web.Seating;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That a cookie outliving its seat does not put somebody in the frame who is not there.
/// </summary>
/// <remarks>
/// <para>
/// Found in use, on the first restart after an upgrade. A seat is an open attachment held in
/// this process — SPEC.md §4.0 makes the connection the evidence — and the cookie is a note in
/// a browser saying which name that person was using. A broker restart ends every seat and
/// touches no cookie, so the two part company and stay parted.
/// </para>
/// <para>
/// The frame believed the cookie. It rendered the name with a live mark beside it while the
/// inbox one click away said "You are seated nowhere, so nothing can be waiting" — the two
/// halves of one page disagreeing about whether a person was present, which is the same defect
/// the frame's own comment records having fixed for the waiting *count* rather than for the
/// seat the count belonged to.
/// </para>
/// <para>
/// Twenty minutes of idleness does it too: <c>SeatBook.IdleAfter</c> releases a seat that a tab
/// left open cannot keep, and the cookie survives that as well.
/// </para>
/// </remarks>
public sealed class StaleSeatTests
{
    [Fact]
    public async Task A_cookie_without_a_seat_is_not_somebody_being_present()
    {
        // DeadBrokerConsole holds no seats at all, which is exactly the state a restarted broker
        // is in: a console serving pages with an empty seat book.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", $"{SeatCookie.Key}=arvind");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Non-vacuous: the page has to have rendered the frame at all before its absence of a
        // name means anything.
        Assert.Contains("Projects", body, StringComparison.Ordinal);

        // The name must not appear as somebody seated, and the way back in must be offered.
        Assert.DoesNotContain("arvind", body, StringComparison.Ordinal);
        Assert.Contains("/Join", body, StringComparison.Ordinal);
    }
}
