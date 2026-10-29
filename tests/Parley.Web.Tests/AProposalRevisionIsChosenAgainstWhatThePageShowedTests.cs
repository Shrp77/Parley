using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That a revision confirmed on the proposal page is confirmed against the revision of the roadmap the page showed
/// (SPEC.md §10.2), whatever the page drew in between, and never against another roadmap's.
/// </summary>
/// <remarks>
/// The page carries the revision it showed, and the roadmap that revision is of. A roadmap that moved since confirms
/// nothing and says so before any warning, and a hint that now matches another roadmap drops the kept revision on every
/// path that draws the page again, so a revision the person never saw is never sent.
/// </remarks>
public sealed class AProposalRevisionIsChosenAgainstWhatThePageShowedTests
{
    private const string Text = "title: Billing export\n1. One";

    private static Uri ProposalUri(long id) =>
        new($"/Roadmaps/Proposal?project={RoadmapConsole.Project}&id={id}", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Field(string html, string name) =>
        Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

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
        var opened = await OpenAsync(client);

        Assert.Equal("1", Field(opened.Html, "Revision"));

        // Another surface saves an item meanwhile: revision 2.
        summary.Revision = 2;
        summary.LiveItems = 2;
        console.Broker.Details[3] = Detail(2, open);

        var page = await opened.PostAsync(client, new() { ["Text"] = Text, ["Target"] = "3" });

        Assert.Empty(console.Broker.Confirmed);
        Assert.Contains("has changed since this page showed it", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove them and confirm", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3", Text)]
    [InlineData("new", "garbage")]
    public async Task A_revision_kept_for_one_roadmap_is_never_carried_to_another_the_hint_now_matches(string target, string text)
    {
        // Whichever path draws the page again, the chosen roadmap renamed away or a new roadmap's text refused: the hint
        // now matches another roadmap, so the revision kept for the first is dropped and the page shows the second's.
        using var console = new RoadmapConsole();
        var a = new Wire.RoadmapSummary { Id = 3, Number = 1, Title = "Billing export", Revision = 3, LiveItems = 0 };
        var b = new Wire.RoadmapSummary { Id = 4, Number = 2, Title = "Other", Revision = 1, LiveItems = 0 };
        console.Broker.Roadmaps.Add(a);
        console.Broker.Roadmaps.Add(b);
        console.Broker.Details[3] = new Wire.RoadmapDetail { Id = 3, Number = 1, Title = "Billing export", Revision = 3 };
        console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = "Other", Revision = 1 };
        console.Broker.Propose(8, "orders@test", "Billing export", ["One"], target: "billing export");
        using var client = console.CreateClient();
        var opened = await OpenAsync(client);

        // Elsewhere: A renamed away, B renamed to the hint at revision 2.
        a.Title = "Billing export, old";
        a.Revision = 4;
        console.Broker.Details[3] = new Wire.RoadmapDetail { Id = 3, Number = 1, Title = a.Title, Revision = 4 };
        b.Title = "Billing export";
        b.Revision = 2;
        console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = b.Title, Revision = 2 };

        var drawn = await opened.PostAsync(client, new() { ["Text"] = text, ["Target"] = target });

        Assert.Equal("2", Field(drawn, "Revision"));
        Assert.Equal("4", Field(drawn, "RevisionOf"));

        // B is edited elsewhere after the page showed it at 2: what the page carries is now stale, and is refused.
        b.Revision = 3;
        console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = b.Title, Revision = 3 };
        _ = await opened.With(drawn).PostAsync(client, new() { ["Text"] = Text, ["Target"] = "4" });

        Assert.Empty(console.Broker.Confirmed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_revision_the_person_chose_is_never_turned_into_a_new_roadmap_by_a_page_drawn_again(bool another)
    {
        // Round 56: a page drawn again for a problem in the text came before the check that the roadmap chosen still
        // matched, so a renamed roadmap left "A new roadmap" checked, or no choice at all, and the next submit made one.
        using var console = new RoadmapConsole();
        var a = new Wire.RoadmapSummary { Id = 3, Number = 1, Title = "Billing export", Revision = 3, LiveItems = 0 };
        var b = new Wire.RoadmapSummary { Id = 4, Number = 2, Title = "Other", Revision = 1, LiveItems = 0 };
        console.Broker.Roadmaps.Add(a);
        console.Broker.Roadmaps.Add(b);
        console.Broker.Details[3] = new Wire.RoadmapDetail { Id = 3, Number = 1, Title = "Billing export", Revision = 3 };
        console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = "Other", Revision = 1 };
        console.Broker.Propose(8, "orders@test", "Billing export", ["One"], target: "billing export");
        using var client = console.CreateClient();
        var opened = await OpenAsync(client);

        // Elsewhere: A renamed away, and B renamed to the hint or not.
        a.Title = "Billing export, old";
        a.Revision = 4;
        console.Broker.Details[3] = new Wire.RoadmapDetail { Id = 3, Number = 1, Title = a.Title, Revision = 4 };

        if (another)
        {
            b.Title = "Billing export";
            b.Revision = 2;
            console.Broker.Details[4] = new Wire.RoadmapDetail { Id = 4, Number = 2, Title = b.Title, Revision = 2 };
        }

        // The person chose to revise #1, and typed something the codec refuses.
        var drawn = await opened.PostAsync(client, new() { ["Text"] = "garbage", ["Target"] = "3" });

        Assert.Contains("no longer the one this proposal matches", drawn, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Confirmed);
    }

    private static async Task<Opened> OpenAsync(HttpClient client)
    {
        using var opening = new HttpRequestMessage(HttpMethod.Get, ProposalUri(8));
        using var get = await client.SendAsync(opening, Ct);
        var html = await get.Content.ReadAsStringAsync(Ct);
        var antiforgery = Regex.Match(
            html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;
        var cookies = get.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(x => x.Split(';')[0]))
            : string.Empty;

        return new Opened(html, antiforgery, cookies);
    }

    // A page as the person has it: the revision fields it drew go back with every submit, as a browser sends them.
    private sealed record Opened(string Html, string Antiforgery, string Cookies)
    {
        public Opened With(string html) => this with { Html = html };

        public async Task<string> PostAsync(HttpClient client, Dictionary<string, string> form)
        {
            form["__RequestVerificationToken"] = Antiforgery;
            form["Revision"] = Field(Html, "Revision");
            form["RevisionOf"] = Field(Html, "RevisionOf");

            using var posting = new HttpRequestMessage(HttpMethod.Post, ProposalUri(8)) { Content = new FormUrlEncodedContent(form) };

            if (Cookies.Length > 0)
            {
                posting.Headers.Add("Cookie", Cookies);
            }

            using var posted = await client.SendAsync(posting, Ct);

            return await posted.Content.ReadAsStringAsync(Ct);
        }
    }
}
