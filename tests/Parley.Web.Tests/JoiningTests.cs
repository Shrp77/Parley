using System.Net;
using System.Text.RegularExpressions;
using Parley.Web.Seating;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Joining and leaving as they are reached through the console rather than through the book.
/// </summary>
/// <remarks>
/// <c>SeatTests</c>, now in <c>Parley.Client.Tests</c> with the book it probes, covers the
/// rules; these probe the surface, which is where two of them
/// can be got round. A departure reachable by following a link is a departure a page-crawler can
/// cause, and a composer that does not say which voice it is speaking in is the most misleading
/// thing that could sit at the bottom of a chat.
/// </remarks>
public sealed class JoiningTests
{
    [Fact]
    public async Task Leaving_is_not_something_a_link_can_do()
    {
        // Leaving drops the attachment, and SPEC.md §4.0 makes dropping one a departure. Every
        // other operation in this console that changes something is a POST for exactly this
        // reason; this one is easy to overlook because it reads like navigation.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/Leave");
        request.Headers.Add("Cookie", "parley.seat=arvind");

        using var response = await client.SendAsync(
            request, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Leave as arvind?", body, StringComparison.Ordinal);

        // And nothing was cleared. A GET that quietly logged somebody out while showing them a
        // confirmation would be the worst of both.
        Assert.DoesNotContain("You have left", body, StringComparison.Ordinal);
        Assert.False(
            response.Headers.TryGetValues("Set-Cookie", out var written)
            && written.Any(value => value.Contains("parley.seat=;", StringComparison.Ordinal)),
            "a GET cleared the seat cookie, so following a link logged somebody out");
    }

    [Fact]
    public async Task The_composer_says_it_instructs_when_nobody_has_joined()
    {
        // The default, and the one that must never quietly become the other. Without a seat
        // there is no name in the registry to send as, and a box that looked like a chat box
        // would have an operator believe they had spoken to a conversation they had not entered.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri(
                $"/Conversations/Detail?project={DeadBrokerConsole.Project}&id=any",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("as an instruction from you", body, StringComparison.Ordinal);
        Assert.Contains("not</strong> appear in the transcript", body, StringComparison.Ordinal);

        // And it offers the other voice rather than leaving somebody to find it.
        Assert.Contains("/Join?Then=", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_join_that_seats_nobody_does_not_remember_the_name()
    {
        // The broker here is a port nothing listens on, so no seat can be taken. A console that
        // wrote the cookie anyway would show somebody joined in its own frame while showing them
        // in no roster anywhere — the two halves disagreeing, with the frame being the half a
        // person reads.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(client, "/Join", new()
        {
            ["Person"] = "arvind"
        });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Seated as", body, StringComparison.Ordinal);
        Assert.False(
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("parley.seat=arvind", StringComparison.Ordinal)),
            "the console remembered a name it seated nowhere");
    }

    [Fact]
    public async Task The_frame_offers_a_way_in_on_every_page()
    {
        // In the frame rather than on a page, because being present is a fact about the
        // deployment and not about whichever page happens to be open.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        foreach (var page in new[] { "/", $"/Projects/Agents?project={DeadBrokerConsole.Project}" })
        {
            var body = await client.GetStringAsync(
                new Uri(page, UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.Contains("class=\"seat-join\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_cookie_somebody_edited_by_hand_is_not_a_name()
    {
        // The cookie arrives from a client and the name in it is about to be rendered into a
        // page and, if it named a seat, written into a registry. It is validated on the way out
        // exactly as strictly as on the way in.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/Leave");
        request.Headers.Add("Cookie", "parley.seat=<script>alert(1)</script>");

        using var response = await client.SendAsync(
            request, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Nobody is here", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lapsed_seat_refuses_rather_than_quietly_instructing_instead()
    {
        // The composer has two voices and the panel says which one it is using. A seat released
        // by the idle sweep between rendering the page and posting it would fall through to the
        // other voice: an instruction sent, nothing in the transcript, and a person who had just
        // been told they were in the conversation as themselves.
        //
        // The cookie survives the sweep by design -- the console still knows who you are -- so
        // this state is reached in the ordinary course of leaving a page open, not by an edit.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(
            client,
            $"/Conversations/Detail?project={DeadBrokerConsole.Project}&id=any",
            new()
            {
                ["Targets"] = $"orders@{DeadBrokerConsole.Project}",
                ["Says"] = "anything at all"
            },
            seat: "arvind");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("no longer held", body, StringComparison.Ordinal);

        // And nothing was sent by either voice. An outcome row would mean something went out.
        Assert.DoesNotContain("<li><strong>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_takes_effect_on_the_page_that_does_it()
    {
        // A cookie is written to the response and the frame reads the request, so the page that
        // changes who is here renders from what the request arrived with — the header offering
        // to leave somebody who has just left, correcting itself on the next navigation. That is
        // the kind of wrong that survives being looked at, which is why it is asserted.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(
            client, "/Leave", [], seat: "arvind");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("You have left", body, StringComparison.Ordinal);

        // Matched on the attribute rather than on the class name, because the stylesheet is
        // inlined in this page and names every class in it. The first version of this assertion
        // passed against the CSS rule and proved nothing about the frame.
        Assert.DoesNotContain("class=\"seat-leave\"", body, StringComparison.Ordinal);
        Assert.Contains("class=\"seat-join\"", body, StringComparison.Ordinal);
    }

    /// <summary>Posts a form, carrying the antiforgery token the console requires.</summary>
    /// <param name="client">The client to post with.</param>
    /// <param name="path">Where to post.</param>
    /// <param name="fields">The form fields.</param>
    /// <param name="seat">A seat to arrive holding, or null to arrive with none.</param>
    /// <returns>The response.</returns>
    /// <remarks>
    /// The token is fetched from the page's own GET rather than disabled, because a test that
    /// turned antiforgery off would be testing a console nobody runs.
    /// </remarks>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> fields,
        string? seat = null)
    {
        var token = TestContext.Current.CancellationToken;

        using var opening = new HttpRequestMessage(HttpMethod.Get, path);

        if (seat is not null)
        {
            opening.Headers.Add("Cookie", $"{SeatCookie.Key}={seat}");
        }

        using var page = await client.SendAsync(opening, token);

        var html = await page.Content.ReadAsStringAsync(token);

        var value = Regex.Match(
            html,
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.NotEmpty(value);

        fields["__RequestVerificationToken"] = value;

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields)
        };

        var carried = page.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).ToList()
            : [];

        if (seat is not null)
        {
            carried.Add($"{SeatCookie.Key}={seat}");
        }

        if (carried.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", carried));
        }

        return await client.SendAsync(request, token);
    }
}
