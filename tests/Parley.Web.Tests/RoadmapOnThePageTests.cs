using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// A confirmed roadmap on the web console: its items, where each stands, and a way into each one's conversation (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The terminal opens a roadmap and the console did not</b>, so a person confirming one here could see it counted and
/// never see its items. These hold the page to what the terminal shows, and to the one thing starting an item must not do
/// on either: send anything.
/// </para>
/// </remarks>
public sealed class RoadmapOnThePageTests
{
    private static readonly Uri IndexUri = new($"/Roadmaps?project={RoadmapConsole.Project}", UriKind.Relative);

    private static Uri PageUri(long id) =>
        new($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id={id}", UriKind.Relative);

    private static Wire.RoadmapItemDetail Item(int number, string text, string agent, bool started, params int[] needs)
    {
        var item = new Wire.RoadmapItemDetail
        {
            Id = number,
            Number = number,
            Position = number,
            Text = text,
            IntendedAgent = agent,
            ConversationName = $"rm1-{number}",
            Facts = new Wire.ItemFacts { ConversationExists = started },
        };

        item.Needs.AddRange(needs);

        return item;
    }

    private static RoadmapConsole Confirmed(params Wire.RoadmapItemDetail[] items)
    {
        var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        detail.Items.AddRange(items);
        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary
        {
            Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = items.Length, StartedItems = 0,
        });

        return console;
    }

    [Fact]
    public async Task The_index_links_each_roadmap_to_its_own_page()
    {
        // Must fail against a list that names a roadmap and goes nowhere.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(IndexUri, TestContext.Current.CancellationToken);

        Assert.Contains("/Roadmaps/Roadmap?project=alpha&amp;id=1", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_lists_the_items_with_where_each_stands_and_what_comes_next()
    {
        using var console = Confirmed(
            Item(1, "Add CSV export", "billing@test", started: false),
            Item(2, "Call it from the report", "orders@test", started: false, needs: 1));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.Contains("#1 Billing export", page, StringComparison.Ordinal);
        Assert.Contains("Add CSV export", page, StringComparison.Ordinal);
        Assert.Contains("not started", page, StringComparison.Ordinal);
        Assert.Contains("Next: item 1.", page, StringComparison.Ordinal);
        Assert.Contains("for billing@test", page, StringComparison.Ordinal);
        Assert.Contains("needs 1", page, StringComparison.Ordinal);
        Assert.Contains("Agents on this roadmap: billing@test, orders@test.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_not_started_opens_its_conversation_with_its_words_and_every_roadmap_agent_ready()
    {
        // The terminal does this on Enter. Must fail against a link that carries neither the words nor the agents.
        using var console = Confirmed(
            Item(1, "Add CSV export", "billing@test", started: false),
            Item(2, "Call it from the report", "orders@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.Contains("id=rm1-1&amp;Hint=Add%20CSV%20export", page, StringComparison.Ordinal);
        Assert.Contains("&amp;With=billing%40test&amp;With=orders%40test", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_items_conversation_names_the_item_and_every_page_names_the_project()
    {
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("<p class=\"item-title\">Add CSV export</p>", page, StringComparison.Ordinal);
        Assert.Contains($"class=\"crumb\" href=\"/Projects/Agents?project={RoadmapConsole.Project}\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_carrying_words_never_puts_them_in_the_box()
    {
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&Says=Add%20CSV%20export", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(">Add CSV export</textarea>", page, StringComparison.Ordinal);
        Assert.Contains("></textarea>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_already_started_links_to_its_conversation_and_prefills_nothing()
    {
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: true));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        // Shown in the roadmap, with a way out to the conversation page.
        Assert.Contains("class=\"item-frame\"", page, StringComparison.Ordinal);
        Assert.Contains("&amp;embed=1", page, StringComparison.Ordinal);
        Assert.Contains(">Pop out</a>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Hint=", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_asked_for_embedded_drops_the_consoles_frame_and_a_roadmap_item_embeds_it()
    {
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        var embedded = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&embed=1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var plain = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var roadmap = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.Matches("<body class=\"[^\"]* embed", embedded);
        Assert.DoesNotMatch("<body class=\"[^\"]* embed", plain);
        Assert.Contains("data-src=\"/Conversations/Detail?project=" + RoadmapConsole.Project + "&amp;id=rm1-1&amp;Hint=", roadmap, StringComparison.Ordinal);
        Assert.Contains("&amp;embed=1\"", roadmap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Following_the_link_sends_nothing()
    {
        // A GET describes and a POST acts. The broker records requests; there is no confirm, edit or decline.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        _ = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.Empty(console.Broker.Confirmed);
        Assert.Empty(console.Broker.Declined);
        Assert.Empty(console.Broker.Edited);
    }

    [Fact]
    public async Task An_items_words_are_escaped()
    {
        using var console = Confirmed(Item(1, "<script>alert(1)</script>", "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<script>alert(1)</script>", page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roadmap_that_does_not_exist_says_so()
    {
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(99), TestContext.Current.CancellationToken);

        Assert.Contains("There is no such roadmap in this project.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_conversation_page_fills_the_box_and_ticks_the_agents_a_link_names()
    {
        // Must fail against a conversation page that ignores the link's words and agents.
        using var console = new TranscriptConsole();

        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "billing@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "orders@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri(
                $"/Conversations/Detail?project={TranscriptConsole.Project}&id=rm1-1&Hint=Add%20CSV%20export&With=billing%40test",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        // A hint, not words: the box itself is empty.
        Assert.Contains("placeholder=\"Add CSV export\"", page, StringComparison.Ordinal);
        Assert.Contains("></textarea>", page, StringComparison.Ordinal);
        Assert.Matches("name=\"Targets\" value=\"billing@test\" checked=\"checked\"", page);
        Assert.DoesNotMatch("name=\"Targets\" value=\"orders@test\" checked", page);
    }

    [Fact]
    public void The_first_message_on_an_item_says_which_item_and_roadmap_it_is_about()
    {
        var sent = Parley.Web.Pages.Conversations.DetailModel.Lead("Add CSV export", "Billing export", "Start with the header row.");

        Assert.Equal(
            "This is about \"Add CSV export\" in the roadmap \"Billing export\": Start with the header row.", sent);
    }
}
