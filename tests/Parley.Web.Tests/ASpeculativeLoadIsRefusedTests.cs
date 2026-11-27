using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That a page another site asks the browser to prefetch or prerender never runs.</summary>
/// <remarks>
/// A page on another loopback port with speculation rules makes Chrome fetch a console page with
/// <c>Sec-Fetch-Site: none</c> and <c>Sec-Purpose: prefetch;prerender</c>; the cross-site gate admitted it and the Inbox
/// recorded the person's mail delivered (review, lens 2 run S3 on 3b752970, a real Chrome against a real parley-web-ui).
/// </remarks>
[Integration]
public sealed class ASpeculativeLoadIsRefusedTests
{
    [Theory]
    [InlineData("prefetch")]
    [InlineData("prefetch;prerender")]
    [InlineData("prefetch;anonymous-client-ip")]
    public async Task A_speculative_load_is_refused_before_any_page_runs(string purpose)
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/Inbox", UriKind.Relative));
        request.Headers.Add("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Purpose", purpose);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
