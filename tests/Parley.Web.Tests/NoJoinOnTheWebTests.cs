using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console has no Join and no Leave, and that its composer never quietly becomes an operator's instruction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Was <c>JoiningTests</c> (T032).</b> It probed joining and leaving through the console: a Leave no link could
/// reach, a join that seated nobody not remembering the name, a hand-edited cookie, the frame offering a way in on every
/// page. All of it is gone — the broker provides the person seat, every page takes it, and the cookie that
/// remembered a typed name is gone — so those properties have nothing left to hold.
/// </para>
/// <para>
/// <b>One property survives and matters more than it did</b>: the composer must never send an instruction because no seat
/// was held. It used to fall back to one on purpose, and then to refuse when a seat had lapsed; now it has one voice, the
/// person's, and when that cannot be had it sends nothing and says why. Probed against a broker nothing listens on,
/// which is exactly when the fallback used to fire.
/// </para>
/// </remarks>
public sealed class NoJoinOnTheWebTests
{
    [Theory]
    [InlineData("/Join")]
    [InlineData("/Leave")]
    public async Task The_pages_that_took_and_gave_back_a_seat_are_gone(string path)
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task No_page_offers_to_join_or_to_leave()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        foreach (var page in new[] { "/", $"/Projects/Agents?project={DeadBrokerConsole.Project}", "/Inbox" })
        {
            var body = await client.GetStringAsync(new Uri(page, UriKind.Relative), TestContext.Current.CancellationToken);

            // Matched on attributes and hrefs rather than class names alone: the stylesheet is inlined in every page and
            // names every class in it, which made a first version of an assertion like this pass against the CSS.
            Assert.DoesNotContain("href=\"/Join", body, StringComparison.Ordinal);
            Assert.DoesNotContain("action=\"/Leave\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_inbox_needs_no_name_and_says_it_is_not_connected()
    {
        // It redirected to Join when nobody had typed a name. There is always somebody it is about now.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri("/Inbox", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Not connected as you just now", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_composer_never_says_it_instructs()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={DeadBrokerConsole.Project}&id=any", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("as an instruction from you", body, StringComparison.Ordinal);
        // The note about the seat is gone: sending takes it and says why if it cannot.
        Assert.DoesNotContain("Not connected to this project as you", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_seat_to_be_had_posting_sends_nothing_and_says_why()
    {
        // The property this file exists to keep. Against a broker nothing listens on, the old composer instructed.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(
            client,
            $"/Conversations/Detail?project={DeadBrokerConsole.Project}&id=any",
            new()
            {
                ["Targets"] = $"orders@{DeadBrokerConsole.Project}",
                ["Says"] = "anything at all"
            });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Nothing was sent", body, StringComparison.Ordinal);

        // And nothing was sent by any voice. An outcome row would mean something went out.
        Assert.DoesNotContain("<li><strong>", body, StringComparison.Ordinal);
    }

    /// <summary>Posts a form, carrying the antiforgery token the console requires.</summary>
    /// <param name="client">The client to post with.</param>
    /// <param name="path">Where to post.</param>
    /// <param name="fields">The form fields.</param>
    /// <returns>The response.</returns>
    /// <remarks>
    /// The token is fetched from the page's own GET rather than disabled, because a test that turned antiforgery off
    /// would be testing a console nobody runs.
    /// </remarks>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, Dictionary<string, string> fields)
    {
        var token = TestContext.Current.CancellationToken;

        using var page = await client.GetAsync(new Uri(path, UriKind.Relative), token);

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

        if (page.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        return await client.SendAsync(request, token);
    }
}
