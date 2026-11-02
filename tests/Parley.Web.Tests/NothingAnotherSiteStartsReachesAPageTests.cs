using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That a request another site started never reaches a page, since a page here acts on a GET.
/// </summary>
/// <remarks>
/// <b>Found by review, lens 2 run 66.</b> Opening the inbox takes the person's seat and records their mail as delivered.
/// An image on any site the person visits sends that GET with the console's own Host, which the host check admits, so any
/// page could forge the delivery record, tell senders their message was picked up, and take mail away from the
/// terminal's bell.
/// </remarks>
[Integration]
public sealed class NothingAnotherSiteStartsReachesAPageTests
{
    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task A_request_another_site_started_is_refused_before_any_page_runs(string site)
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/Inbox", UriKind.Relative));
        request.Headers.Add("Sec-Fetch-Site", site);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "answers only its own pages",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    [InlineData(null)]
    public async Task The_consoles_own_pages_and_a_typed_address_are_answered(string? site)
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/Inbox", UriKind.Relative));

        if (site is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", site);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
