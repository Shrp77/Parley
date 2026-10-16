using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// Changing a confirmed roadmap on the web console: removing, rewording and adding items (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>A roadmap an agent proposed may hold work the person does not want</b>, and confirming it must not be the last
/// chance to say so. A GET describes the roadmap as text and sends nothing; a POST sends the content the person wrote, at
/// the revision the form was opened at.
/// </para>
/// </remarks>
public sealed class RoadmapEditTests
{
    private static Uri EditUri() =>
        new($"/Roadmaps/Edit?project={RoadmapConsole.Project}&id=1", UriKind.Relative);

    private static Uri RoadmapUri() =>
        new($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id=1", UriKind.Relative);

    private static Wire.RoadmapItemDetail Item(int number, string text, string agent, params int[] needs)
    {
        var item = new Wire.RoadmapItemDetail
        {
            Id = number,
            Number = number,
            Position = number,
            Text = text,
            IntendedAgent = agent,
            ConversationName = $"rm1-{number}",
            Facts = new Wire.ItemFacts { ConversationExists = false },
        };

        item.Needs.AddRange(needs);

        return item;
    }

    private static RoadmapConsole Confirmed()
    {
        var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        detail.Items.Add(Item(1, "Add CSV export", "billing@test"));
        detail.Items.Add(Item(2, "Call it from the report", "orders@test", 1));
        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary
        {
            Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 2, StartedItems = 0,
        });

        return console;
    }

    private static async Task<string> SaveAsync(HttpClient client, string text, int revision = 1)
    {
        var response = await PostAsync(
            client,
            EditUri(),
            new Dictionary<string, string> { ["Text"] = text, ["Revision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_roadmap_page_links_to_its_edit_page()
    {
        using var console = Confirmed();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(RoadmapUri(), TestContext.Current.CancellationToken);

        Assert.Contains("/Roadmaps/Edit?project=alpha&amp;id=1", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_edit_page_shows_the_current_text_and_sends_nothing()
    {
        using var console = Confirmed();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(EditUri(), TestContext.Current.CancellationToken);

        Assert.Contains("title: Billing export", page, StringComparison.Ordinal);
        Assert.Contains("1. Add CSV export | for: billing@test | item: 1", page, StringComparison.Ordinal);
        Assert.Contains("2. Call it from the report | for: orders@test | needs: 1 | item: 2", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Revision\" value=\"1\"", page, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Edited);
    }

    [Fact]
    public async Task Saving_sends_the_words_at_the_revision_and_the_roadmap_page_shows_the_change()
    {
        using var console = Confirmed();
        using var client = console.CreateClient();

        var body = await SaveAsync(
            client, "title: Billing export\n1. Add CSV export, with headers | for: billing@test | item: 1\n2. Add a JSON export | needs: 1");

        var sent = Assert.Single(console.Broker.Edited);
        Assert.Equal(1, sent.Id);
        Assert.Equal(1, sent.Revision);
        Assert.Equal(1, sent.Content.Items[0].Number);
        Assert.Equal("Add CSV export, with headers", sent.Content.Items[0].Text);
        Assert.Equal(0, sent.Content.Items[1].Number);
        Assert.Equal("Add a JSON export", sent.Content.Items[1].Text);
        Assert.Contains("Saved", body, StringComparison.Ordinal);

        var page = await client.GetStringAsync(RoadmapUri(), TestContext.Current.CancellationToken);

        Assert.Contains("Add CSV export, with headers", page, StringComparison.Ordinal);
        Assert.Contains("Add a JSON export", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Call it from the report", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_revision_is_reported_and_not_applied_and_the_typed_text_is_kept()
    {
        using var console = Confirmed();
        console.Broker.Details[1].Revision = 2;
        using var client = console.CreateClient();

        var body = await SaveAsync(client, "title: Mine\n1. My own wording | item: 1");

        Assert.Empty(console.Broker.Edited);
        Assert.Contains("changed after you opened it", body, StringComparison.Ordinal);
        Assert.Contains("1. My own wording | item: 1", body, StringComparison.Ordinal);
        Assert.Contains("2. Call it from the report", body, StringComparison.Ordinal);
        Assert.Contains("name=\"Revision\" value=\"2\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_that_is_not_a_roadmap_is_refused_in_the_page_and_nothing_is_sent()
    {
        using var console = Confirmed();
        using var client = console.CreateClient();

        var body = await SaveAsync(client, "title: P\n1. First | needs: 2\n2. Second");

        Assert.Empty(console.Broker.Edited);
        Assert.Contains("not an earlier item", body, StringComparison.Ordinal);
        Assert.Contains("1. First | needs: 2", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_from_the_broker_is_shown_beside_the_box_with_the_text_kept()
    {
        using var console = Confirmed();
        console.Broker.Refuse = "nobody@test is not an agent in this project, so an item cannot be meant for it.";
        using var client = console.CreateClient();

        var body = await SaveAsync(client, "title: P\n1. Do it | for: nobody@test");

        Assert.Single(console.Broker.Edited);
        Assert.Contains("is not an agent in this project", body, StringComparison.Ordinal);
        Assert.Contains("1. Do it | for: nobody@test", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roadmap_that_does_not_exist_says_so_on_the_edit_page()
    {
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(EditUri(), TestContext.Current.CancellationToken);

        Assert.Contains("There is no such roadmap in this project.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea", page, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Uri path, Dictionary<string, string> fields)
    {
        var token = TestContext.Current.CancellationToken;

        using var opening = new HttpRequestMessage(HttpMethod.Get, path);
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
