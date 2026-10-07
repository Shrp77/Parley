using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// Reviewing the roadmaps agents have proposed, on the web console (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>A GET describes and a POST decides</b>, the rule every action on this console is held to, so each test names what
/// the page sent and what it did not. The broker is a script that records its requests, and a page that confirmed on a GET
/// would show up as a confirmation nobody asked for.
/// </para>
/// </remarks>
public sealed class ProposalsOnThePageTests
{
    private static readonly Uri IndexUri = new($"/Roadmaps?project={RoadmapConsole.Project}", UriKind.Relative);

    private static Uri ProposalUri(long id) =>
        new($"/Roadmaps/Proposal?project={RoadmapConsole.Project}&id={id}", UriKind.Relative);

    private static Uri DeclineUri(long id) =>
        new($"/Roadmaps/Decline?project={RoadmapConsole.Project}&id={id}", UriKind.Relative);

    private static RoadmapConsole Proposed()
    {
        var console = new RoadmapConsole();

        console.Broker.Propose(7, "orders@test", "Billing export", ["Add CSV export", "Call it from the report"]);

        return console;
    }

    [Fact]
    public async Task The_index_lists_what_is_waiting_and_the_roadmaps_with_a_way_in_to_each_proposal()
    {
        using var console = Proposed();
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 1, Number = 1, Title = "Earlier plan", Revision = 2, LiveItems = 3, StartedItems = 1 });
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(IndexUri, TestContext.Current.CancellationToken);

        Assert.Contains("awaiting your decision (1)", page, StringComparison.Ordinal);
        Assert.Contains("Billing export", page, StringComparison.Ordinal);
        Assert.Contains("/Roadmaps/Proposal?project=alpha&amp;id=7", page, StringComparison.Ordinal);
        Assert.Contains("Earlier plan", page, StringComparison.Ordinal);
        Assert.Contains("3 items, 1 started", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_proposed_the_index_says_so()
    {
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(IndexUri, TestContext.Current.CancellationToken);

        Assert.Contains("No roadmap is awaiting your decision.", page, StringComparison.Ordinal);
        Assert.Contains("There is no roadmap in this project yet", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agents_words_in_a_proposal_are_escaped()
    {
        using var console = new RoadmapConsole();
        console.Broker.Propose(7, "orders@test", "<script>alert(1)</script>", ["<b>bold</b> item"]);
        using var client = console.CreateClient();

        var index = await client.GetStringAsync(IndexUri, TestContext.Current.CancellationToken);
        var review = await client.GetStringAsync(ProposalUri(7), TestContext.Current.CancellationToken);

        foreach (var page in new[] { index, review })
        {
            Assert.DoesNotContain("<script>alert(1)</script>", page, StringComparison.Ordinal);
            Assert.DoesNotContain("<b>bold</b>", page, StringComparison.Ordinal);
        }

        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewing_a_proposal_describes_it_and_sends_no_decision()
    {
        using var console = Proposed();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(ProposalUri(7), TestContext.Current.CancellationToken);

        Assert.Contains("Proposed by <strong>orders@test</strong>", page, StringComparison.Ordinal);
        Assert.Contains("assigns nobody", page, StringComparison.Ordinal);

        // The proposal as text, in the box, so confirming it unchanged and confirming an edit are one button.
        Assert.Contains("title: Billing export", page, StringComparison.Ordinal);
        Assert.Contains("1. Add CSV export", page, StringComparison.Ordinal);

        Assert.Empty(console.Broker.Confirmed);
        Assert.Empty(console.Broker.Declined);
    }

    [Fact]
    public async Task Confirming_sends_the_content_the_person_wrote_and_says_the_proposer_was_told()
    {
        using var console = Proposed();
        using var client = console.CreateClient();

        var response = await PostAsync(
            client,
            ProposalUri(7),
            new Dictionary<string, string>
            {
                ["Text"] = "title: Billing export, reworded\n1. Add a CSV export endpoint | for: billing@test",
                ["Target"] = "new",
            });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var sent = Assert.Single(console.Broker.Confirmed);
        Assert.Equal(7, sent.Id);
        Assert.Equal("Billing export, reworded", sent.Content.Title);
        Assert.Equal("Add a CSV export endpoint", Assert.Single(sent.Content.Items).Text);
        Assert.Equal("billing@test", sent.Content.Items[0].IntendedAgent);
        Assert.Equal(0, sent.TargetRoadmap);

        Assert.Contains("Confirmed", body, StringComparison.Ordinal);
        Assert.Contains("orders was told", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_that_is_not_a_roadmap_is_refused_in_the_page_and_nothing_is_sent()
    {
        using var console = Proposed();
        using var client = console.CreateClient();

        var response = await PostAsync(
            client, ProposalUri(7), new Dictionary<string, string> { ["Text"] = "title: P\n1. First | needs: 2\n2. Second" });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Empty(console.Broker.Confirmed);
        Assert.Contains("not an earlier item", body, StringComparison.Ordinal);

        // The person's text is kept in the box for them to fix, not thrown away.
        Assert.Contains("1. First | needs: 2", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_from_the_broker_is_shown_as_its_own_sentence()
    {
        using var console = Proposed();
        console.Broker.Refuse = "nobody@test is not an agent in this project, so an item cannot be meant for it.";
        using var client = console.CreateClient();

        var response = await PostAsync(
            client, ProposalUri(7), new Dictionary<string, string> { ["Text"] = "title: P\n1. Do it" });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("is not an agent in this project", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_matching_title_offers_a_revision_and_a_new_roadmap_with_the_new_one_chosen()
    {
        using var console = new RoadmapConsole();
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 3, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 2 });
        console.Broker.Propose(8, "orders@test", "Billing export v2", ["One"], target: "billing EXPORT");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(ProposalUri(8), TestContext.Current.CancellationToken);

        Assert.Contains("matches the roadmap <strong>Billing export</strong>", page, StringComparison.Ordinal);
        Assert.Matches(new Regex("name=\"Target\" value=\"new\" checked", RegexOptions.None, TimeSpan.FromSeconds(2)), page);
        Assert.Contains("value=\"3\"", page, StringComparison.Ordinal);

        await PostAsync(
            client,
            ProposalUri(8),
            new Dictionary<string, string> { ["Text"] = "title: Billing export v2\n1. One", ["Target"] = "3" });

        Assert.Equal(3, Assert.Single(console.Broker.Confirmed).TargetRoadmap);
    }

    [Fact]
    public async Task Declining_is_described_on_a_get_and_only_a_post_declines()
    {
        using var console = Proposed();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(DeclineUri(7), TestContext.Current.CancellationToken);

        Assert.Contains("Declining keeps no roadmap", page, StringComparison.Ordinal);
        Assert.Contains("is told", page, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Declined);

        var response = await PostAsync(client, DeclineUri(7), []);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, Assert.Single(console.Broker.Declined));
        Assert.Contains("Declined", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposal_that_is_not_waiting_says_so_and_decides_nothing()
    {
        using var console = Proposed();
        using var client = console.CreateClient();

        var review = await client.GetStringAsync(ProposalUri(99), TestContext.Current.CancellationToken);
        var decline = await PostAsync(client, DeclineUri(99), [], tokenFrom: DeclineUri(7));

        Assert.Contains("not awaiting your decision", review, StringComparison.Ordinal);
        Assert.Contains("not awaiting your decision", await decline.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(console.Broker.Declined);
        Assert.Empty(console.Broker.Confirmed);
    }

    [Fact]
    public async Task The_project_page_links_to_the_roadmaps()
    {
        using var console = Proposed();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={RoadmapConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains($"/Roadmaps?project={RoadmapConsole.Project}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_project_list_counts_what_is_proposed_with_what_else_is_in_the_project()
    {
        using var console = Proposed();
        console.Broker.Propose(8, "billing@test", "Another", ["One"]);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("2 roadmaps proposed, for you to decide", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_cannot_count_proposals_leaves_the_count_out_rather_than_showing_none()
    {
        // Unknown is not none: a broker older than roadmaps answers UNIMPLEMENTED, and a project list that said "0" would
        // read as a project with nothing for the person to decide.
        using var console = Proposed();
        console.Broker.Old = true;
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("proposed, for you to decide", page, StringComparison.Ordinal);
        Assert.DoesNotContain("0 roadmaps", page, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, Uri path, Dictionary<string, string> fields, Uri? tokenFrom = null)
    {
        var token = TestContext.Current.CancellationToken;

        // A page that has no form has no antiforgery token, which is the case when the proposal is not waiting: the token
        // is taken from a page that does.
        using var opening = new HttpRequestMessage(HttpMethod.Get, tokenFrom ?? path);
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
            Content = new FormUrlEncodedContent(fields),
        };

        var carried = page.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).ToList()
            : [];

        if (carried.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", carried));
        }

        return await client.SendAsync(request, token);
    }
}
