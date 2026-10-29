using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That a revision confirmed on the proposal page is confirmed against the revision of the roadmap the page showed
/// (SPEC.md §10.2), however many times the page is drawn again first.
/// </summary>
/// <remarks>
/// <b>Found by review, round 58.</b> The page drew its hidden revision from each read, so when the roadmap moved after it
/// was opened and the revision would remove open work, the warning's page carried the new revision and its second
/// submit replaced the item another surface had saved meanwhile. The page now keeps the revision it showed, and a moved
/// roadmap confirms nothing and says so before any warning.
/// </remarks>
public sealed class AProposalRevisionIsChosenAgainstWhatThePageShowedTests
{
    private const string Text = "title: Billing export\n1. One";

    private static Uri ProposalUri(long id) =>
        new($"/Roadmaps/Proposal?project={RoadmapConsole.Project}&id={id}", UriKind.Relative);

    private static Wire.RoadmapDetail Detail(int revision, bool open)
    {
        var detail = new Wire.RoadmapDetail { Id = 3, Number = 1, Title = "Billing export", Revision = revision };
        detail.Items.Add(new Wire.RoadmapItemDetail
        {
            Id = 1, Number = 1, Position = 1, Text = "Add CSV export", IntendedAgent = "billing@test", ConversationName = "rm1-1",
            Facts = new Wire.ItemFacts { ConversationExists = open, OpenExchanges = open ? 1 : 0 },
        });

        if (revision > 1)
        {
            detail.Items.Add(new Wire.RoadmapItemDetail
            {
                Id = 2, Number = 2, Position = 2, Text = "Saved meanwhile by the terminal", ConversationName = "rm1-2",
            });
        }

        return detail;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_roadmap_that_moved_after_the_page_was_opened_confirms_nothing_and_says_so(bool open)
    {
        using var console = new RoadmapConsole();
        var summary = new Wire.RoadmapSummary { Id = 3, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 1 };
        console.Broker.Roadmaps.Add(summary);
        console.Broker.Details[3] = Detail(1, open);
        console.Broker.Propose(8, "orders@test", "Billing export", ["One"], target: "billing export");
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;

        // The person opens the proposal: the page shows revision 1.
        using var opening = new HttpRequestMessage(HttpMethod.Get, ProposalUri(8));
        using var get = await client.SendAsync(opening, token);
        var html = await get.Content.ReadAsStringAsync(token);
        var shown = Regex.Match(html, "name=\"Revision\" value=\"(\\d+)\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;
        var antiforgery = Regex.Match(
            html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;
        var cookies = get.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(x => x.Split(';')[0]))
            : string.Empty;

        Assert.Equal("1", shown);

        // Another surface saves an item meanwhile: revision 2.
        summary.Revision = 2;
        summary.LiveItems = 2;
        console.Broker.Details[3] = Detail(2, open);

        using var posting = new HttpRequestMessage(HttpMethod.Post, ProposalUri(8))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Text"] = "title: Billing export\n1. One",
                ["Target"] = "3",
                ["Revision"] = shown,
                ["__RequestVerificationToken"] = antiforgery,
            }),
        };

        if (cookies.Length > 0)
        {
            posting.Headers.Add("Cookie", cookies);
        }

        using var posted = await client.SendAsync(posting, token);
        var page = await posted.Content.ReadAsStringAsync(token);

        Assert.Empty(console.Broker.Confirmed);
        Assert.Contains("has changed since this page showed it", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove them and confirm", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revision_kept_for_one_roadmap_is_never_carried_to_another_the_hint_now_matches()
    {
        // Round 55: when the roadmap chosen was renamed away and another took the hint, the page kept the first one's
        // revision for the second, and a revision the person was never shown could pass.
        using var console = new RoadmapConsole();
        var a = new Wire.RoadmapSummary { Id = 3, Number = 1, Title = "Billing export", Revision = 3, LiveItems = 0 };
        var b = new Wire.RoadmapSummary { Id = 4, Number = 2, Title = "Other", Revision = 1, LiveItems = 0 };
        console.Broker.Roadmaps.Add(a);
        console.Broker.Roadmaps.Add(b);
        console.Broker.Details[3] = new Wire.RoadmapDetail { Id = 3, Number = 1, Title = "Billing export", Revision = 3 };
        console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = "Other", Revision = 1 };
        console.Broker.Propose(8, "orders@test", "Billing export", ["One"], target: "billing export");
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;

        using var opening = new HttpRequestMessage(HttpMethod.Get, ProposalUri(8));
        using var get = await client.SendAsync(opening, token);
        var html = await get.Content.ReadAsStringAsync(token);
        var antiforgery = Regex.Match(
            html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;
        var cookies = get.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(x => x.Split(';')[0]))
            : string.Empty;

        // Elsewhere: A renamed away, B renamed to the hint at revision 2.
        a.Title = "Billing export, old";
        a.Revision = 4;
        console.Broker.Details[3] = new Wire.RoadmapDetail { Id = 3, Number = 1, Title = a.Title, Revision = 4 };
        b.Title = "Billing export";
        b.Revision = 2;
        console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = b.Title, Revision = 2 };

        var page = await PostAsync(client, antiforgery, cookies, new() { ["Text"] = Text, ["Target"] = "3", ["Revision"] = "3" });
        var carried = Regex.Match(page, "name=\"Revision\" value=\"(\\d+)\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.Contains("no longer the one this proposal matches", page, StringComparison.Ordinal);
        Assert.Equal("2", carried);

        // B is edited elsewhere after the page showed it at 2: the revision the page carries is now stale.
        b.Revision = 3;
        console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = b.Title, Revision = 3 };
        _ = await PostAsync(client, antiforgery, cookies, new() { ["Text"] = Text, ["Target"] = "4", ["Revision"] = carried });

        Assert.Empty(console.Broker.Confirmed);
    }

    private static async Task<string> PostAsync(HttpClient client, string antiforgery, string cookies, Dictionary<string, string> form)
    {
        form["__RequestVerificationToken"] = antiforgery;
        using var posting = new HttpRequestMessage(HttpMethod.Post, ProposalUri(8)) { Content = new FormUrlEncodedContent(form) };

        if (cookies.Length > 0)
        {
            posting.Headers.Add("Cookie", cookies);
        }

        using var posted = await client.SendAsync(posting, TestContext.Current.CancellationToken);

        return await posted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
