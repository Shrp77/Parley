using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>The embedded layout's click script sends a link without a target to the whole window, so the conversation
/// page's own in-page links must name one, or a tick clicked in the roadmap's frame takes the person off the roadmap.</summary>
[Integration]
public sealed class FragmentLinksStayInTheFrameTests
{
    [Fact]
    public async Task Every_in_page_link_of_the_framed_conversation_names_its_target_so_it_stays_in_the_frame()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(5, "orders@test", "billing@test", "Add CSV export");
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;

        using var get = new HttpRequestMessage(HttpMethod.Get, new Uri($"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared&embed=1", UriKind.Relative));
        get.Headers.Add("Sec-Fetch-Dest", "iframe");
        get.Headers.Add("Sec-Fetch-Mode", "navigate");
        get.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var page = await client.SendAsync(get, token);
        var html = await page.Content.ReadAsStringAsync(token);

        Assert.Contains("data-embed-links", html, StringComparison.Ordinal);

        // The script's own predicate: an <a href> with no target, no download, whose origin is the page's. A fragment-only
        // href resolves against the frame's address, so its origin is the page's and the click navigates the top window to
        // the conversation page with `embed` and the fragment, out of the roadmap.
        var sentToTop = Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Select(m => m.Value)
            .Where(a => Regex.IsMatch(a, "\\bhref=\"#", RegexOptions.None, TimeSpan.FromSeconds(2))
                && !Regex.IsMatch(a, "\\btarget=", RegexOptions.None, TimeSpan.FromSeconds(2))
                && !Regex.IsMatch(a, "\\bdownload\\b", RegexOptions.None, TimeSpan.FromSeconds(2)))
            .ToList();

        Assert.Empty(sentToTop);
    }
}
