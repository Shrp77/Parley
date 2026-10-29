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
    public async Task An_item_not_started_opens_its_conversation_with_its_words_and_its_own_agent_ready()
    {
        // The terminal does this on Enter. Must fail against a link that carries neither the words nor the agent, and
        // against one that ticks every agent the roadmap names: item 1 is billing's, not orders' (SPEC.md §5.7).
        using var console = Confirmed(
            Item(1, "Add CSV export", "billing@test", started: false),
            Item(2, "Call it from the report", "orders@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.Contains("id=rm1-1&amp;With=billing%40test&amp;", page, StringComparison.Ordinal);
        Assert.Contains("id=rm1-2&amp;With=orders%40test&amp;", page, StringComparison.Ordinal);
        Assert.DoesNotContain("With=billing%40test&amp;With=", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_that_is_the_persons_own_opens_with_nobody_ticked()
    {
        // The owner's call, 2026-10-29: an item may be the person's own. Ticking the person would make the start a request
        // to themselves, which is refused; nobody is ticked, and the person chooses.
        using var console = Confirmed(Item(1, "Approve the rate card", "human-user@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.Contains("for human-user@test", page, StringComparison.Ordinal);
        Assert.Contains("id=rm1-1&amp;", page, StringComparison.Ordinal);
        Assert.DoesNotContain("With=human-user", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("billing@test", true)]
    [InlineData("human-user@test", false)]
    public async Task An_items_conversation_ticks_its_agent_but_never_the_person(string agent, bool ticked)
    {
        // What the conversation page draws ticked, whatever the roadmap's link says (lens 3 run 76 found only the link
        // tested). The person's seat is never drawn as a box to tick, so for their own item nothing is.
        using var console = Confirmed(Item(1, "Approve the rate card", agent, started: false));
        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "billing@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "human-user@test", Adapter = "parley-console", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains(">Approve the rate card</textarea>", page, StringComparison.Ordinal);
        Assert.Equal(ticked, page.Contains("checked=\"checked\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_naming_no_agent_opens_its_conversation_with_nobody_ticked()
    {
        // The person chooses from the room, which lists the whole project. Must fail against borrowing another item's agent.
        using var console = Confirmed(
            Item(1, "Add CSV export", string.Empty, started: false),
            Item(2, "Call it from the report", "orders@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);

        Assert.Contains("id=rm1-1&amp;embed=1", page, StringComparison.Ordinal);
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
    public async Task An_items_conversation_links_back_to_its_roadmap_opened_on_that_item()
    {
        // FR-025: from the conversation to its roadmap and item, in one click. Must fail against a page that names the
        // item and offers no way back, or a way back that opens the roadmap on its next item instead.
        using var console = Confirmed(
            Item(1, "Add CSV export", "billing@test", started: true),
            Item(2, "Call it from the report", "orders@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-2", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            $"<a href=\"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&amp;id=1&amp;item=2#item-2\" target=\"_top\">item 2 of #1 Billing export</a>",
            page,
            StringComparison.Ordinal);

        var roadmap = await client.GetStringAsync(
            new Uri($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id=1&item=2", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("id=\"item-2\" open=\"open\"", roadmap, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"item-1\" open=\"open\"", roadmap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asked_for_an_item_it_does_not_show_the_roadmap_opens_on_its_next_item()
    {
        // A removed item's conversation still links here, and a link can name any number. The terminal falls back to the
        // next item; so must this page, rather than opening no row at all. Must fail against opening only the item asked.
        using var console = Confirmed(
            Item(1, "Add CSV export", "billing@test", started: false),
            Item(2, "Call it from the report", "orders@test", started: false));
        using var client = console.CreateClient();

        var roadmap = await client.GetStringAsync(
            new Uri($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id=1&item=9", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("id=\"item-1\" open=\"open\"", roadmap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removed_items_conversation_says_it_formerly_belonged_and_is_not_led_by_it()
    {
        // FR-023a: the conversation stays, belongs to no item, and says which it was.
        var removed = Item(1, "Add CSV export", "billing@test", started: true);
        removed.RemovedRevision = 2;
        using var console = Confirmed(removed);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        // It belongs to no item, so it leads to no roadmap, as in the terminal. Must fail against a link back.
        Assert.Contains("<p class=\"item-of\">formerly item 1 of #1 Billing export</p>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("item=1#item-1", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_projects_conversation_list_marks_item_conversations_and_says_formerly()
    {
        // FR-026 and FR-023a, on the list rather than the page. Must fail against a list that shows rm1-1 bare, and
        // against one that marks a conversation by its name: "rm9-9" carries no reservation and is an ordinary row.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: true));
        console.Broker.Conversations.Add(Summary("rm1-1", new Wire.RoadmapItemRef { RoadmapId = 1, RoadmapNumber = 1, Title = "Billing export", ItemNumber = 1 }));
        console.Broker.Conversations.Add(Summary("rm1-2", new Wire.RoadmapItemRef { RoadmapId = 1, RoadmapNumber = 1, Title = "Billing export", ItemNumber = 2, Removed = true }));
        console.Broker.Conversations.Add(Summary("rm9-9", null));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={RoadmapConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("<span class=\"tag\">item 1 of #1 Billing export</span>", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tag\">formerly item 2 of #1 Billing export</span>", page, StringComparison.Ordinal);
        Assert.Equal(2, page.Split("of #1 Billing export</span>").Length - 1);
    }

    private static Wire.ConversationSummary Summary(string id, Wire.RoadmapItemRef? item)
    {
        var summary = new Wire.ConversationSummary
        {
            ConversationId = id, Messages = 1, FirstActivityUtc = "2026-10-06T09:00:00Z", LastActivityUtc = "2026-10-06T09:00:00Z",
        };

        summary.Participants.Add("billing@test");

        if (item is not null)
        {
            summary.Item = item;
        }

        return summary;
    }

    [Fact]
    public async Task A_conversation_named_like_an_item_but_reserved_by_none_is_an_ordinary_one()
    {
        // FR-024: recognised by the reservation, never by the name. Must fail against matching rm<n>-<m>.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-7", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<p class=\"item-title\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<p class=\"item-of\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("of #1 Billing export", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_carrying_words_never_puts_them_in_the_box()
    {
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=plain-chat&Says=Add%20CSV%20export", UriKind.Relative),
            TestContext.Current.CancellationToken);

        // An ordinary conversation, so nothing but the link could fill the box; an item's own words are a separate case.
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
        var token = TestContext.Current.CancellationToken;
        var asked = new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&embed=1", UriKind.Relative);

        // Loaded into the roadmap's frame, as a browser says it is.
        using var framing = new HttpRequestMessage(HttpMethod.Get, asked);
        framing.Headers.Add("Sec-Fetch-Dest", "iframe");
        using var framed = await client.SendAsync(framing, token);
        var embedded = await framed.Content.ReadAsStringAsync(token);

        // The same address opened as a page of its own, as following an agent's link would.
        using var opened = await client.GetAsync(asked, token);
        var followed = await opened.Content.ReadAsStringAsync(token);

        var plain = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative), token);

        var roadmap = await client.GetStringAsync(PageUri(1), token);

        Assert.Matches("<body class=\"[^\"]* embed", embedded);
        Assert.DoesNotMatch("<body class=\"[^\"]* embed", plain);

        // Must fail against a link with embed hiding the console's frame, and the project's name with it.
        Assert.DoesNotMatch("<body class=\"[^\"]* embed", followed);

        // No other page embeds, even asked from a frame: Act, Destroy and the rest would then name no project.
        using var elsewhere = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id=1&embed=1", UriKind.Relative));
        elsewhere.Headers.Add("Sec-Fetch-Dest", "iframe");
        using var other = await client.SendAsync(elsewhere, token);
        Assert.DoesNotMatch("<body class=\"[^\"]* embed", await other.Content.ReadAsStringAsync(token));

        Assert.Contains("data-src=\"/Conversations/Detail?project=" + RoadmapConsole.Project + "&amp;id=rm1-1&amp;With=billing%40test&amp;embed=1", roadmap, StringComparison.Ordinal);
        Assert.Contains("&amp;embed=1\"", roadmap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_console_frames_its_own_item_conversation_and_neither_a_plain_conversation_nor_the_roadmap_can_be_framed()
    {
        // The clickjacking defence and its one exception. Must fail against the item frame refused by the console's own
        // headers, against the same page opened plainly or the roadmap allowing a frame, and against the frame's links
        // to other pages (the roadmap, Files) opening inside it, where those pages are refused.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;
        var asked = new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&embed=1", UriKind.Relative);

        using var framing = new HttpRequestMessage(HttpMethod.Get, asked);
        framing.Headers.Add("Sec-Fetch-Dest", "iframe");
        using var framed = await client.SendAsync(framing, token);
        var page = await framed.Content.ReadAsStringAsync(token);

        using var opened = await client.GetAsync(asked, token);
        using var roadmap = await client.GetAsync(PageUri(1), token);

        Assert.Equal("SAMEORIGIN", framed.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'self'", framed.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        foreach (var other in new[] { opened, roadmap })
        {
            Assert.Equal("DENY", other.Headers.GetValues("X-Frame-Options").Single());
            Assert.Contains("frame-ancestors 'none'", other.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        }

        // Every other page refuses to be framed, so the frame's way back to the roadmap, and its other links, open in
        // the whole window.
        Assert.Contains("class=\"item-of\"><a href=\"/Roadmaps/Roadmap?project=" + RoadmapConsole.Project, page, StringComparison.Ordinal);
        Assert.Matches("class=\"item-of\"><a href=\"[^\"]*\" target=\"_top\"", page);
        Assert.Matches("class=\"file-button\" target=\"_top\" href=\"/Projects/Files", page);
        Assert.Contains("data-embed-links", page, StringComparison.Ordinal);
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
    public async Task The_conversation_page_ticks_the_agents_a_link_names()
    {
        // Must fail against a conversation page that ignores the link's agents.
        using var console = new TranscriptConsole();

        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "billing@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "orders@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri(
                $"/Conversations/Detail?project={TranscriptConsole.Project}&id=c-1&With=billing%40test",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Matches("name=\"Targets\" value=\"billing@test\" checked=\"checked\"", page);
        Assert.DoesNotMatch("name=\"Targets\" value=\"orders@test\" checked", page);
    }

    [Fact]
    public async Task An_unstarted_items_conversation_opens_with_the_items_words_in_the_box()
    {
        // SPEC.md §5.7, as the terminal does: the words are in the box for the person to change and send, read from the
        // item the reservation names and not from the link. Must fail against an empty box, or one filled from a link.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&Says=Something%20else", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains(">Add CSV export</textarea>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Something else", page, StringComparison.Ordinal);
    }

    private static async Task<string> PostDetailAsync(HttpClient client, Uri path, Dictionary<string, string> fields)
    {
        var token = TestContext.Current.CancellationToken;
        var html = await client.GetStringAsync(path, token);
        var value = System.Text.RegularExpressions.Regex.Match(
            html,
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.NotEmpty(value);
        fields["__RequestVerificationToken"] = value;

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        using var response = await client.SendAsync(request, token);

        return await response.Content.ReadAsStringAsync(token);
    }

    [Fact]
    public async Task A_refused_first_send_keeps_the_persons_words_and_not_the_items()
    {
        // The box holds the item's words only when the page opens. After a send, what is in it is what the person sent.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();
        var path = new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&embed=1", UriKind.Relative);

        var page = await PostDetailAsync(client, path, new Dictionary<string, string> { ["Says"] = "My own words", ["Targets"] = "billing@test" });

        Assert.Contains(">My own words</textarea>", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">Add CSV export</textarea>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_box_the_person_emptied_is_not_filled_again_behind_them()
    {
        // Must fail against filling the box on the page a send returns.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();
        var path = new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative);

        var page = await PostDetailAsync(client, path, new Dictionary<string, string> { ["Says"] = string.Empty, ["Targets"] = "billing@test" });

        Assert.Contains("Nothing was written", page, StringComparison.Ordinal);
        Assert.Contains("></textarea>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_that_ticks_another_agent_on_an_items_conversation_ticks_nobody_but_the_items_own()
    {
        // An agent's message can carry a live link to an item's conversation with `With=` naming a different agent; one
        // press of Send would hand that agent the item's words. Must fail against ticking whoever a link names.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "billing@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "inventory@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);
        using var client = console.CreateClient();

        var forged = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&With=inventory%40test", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var own = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&With=billing%40test", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.DoesNotMatch("name=\"Targets\" value=\"inventory@test\" checked", forged);
        Assert.Matches("name=\"Targets\" value=\"billing@test\" checked=\"checked\"", forged);
        Assert.Matches("name=\"Targets\" value=\"billing@test\" checked=\"checked\"", own);

        // Nor does the page's script tick the project's default agent on an item's conversation: who it is for is the item's.
        Assert.DoesNotContain("data-new=\"true\"", forged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_links_Targets_never_chooses_who_the_persons_send_reaches()
    {
        // The form has no action, so it posts back to the page's address, query included. With no box ticked the form
        // carries no Targets, and a link's would bind and send the item's words to the agent it named while the page said
        // nobody was chosen. Must fail against binding posted fields from the query.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();
        var forged = new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&Targets=inventory%40test", UriKind.Relative);

        var page = await PostDetailAsync(client, forged, new Dictionary<string, string> { ["Says"] = "Add CSV export" });

        Assert.Contains("Nobody was picked, so nothing was sent.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_links_Everyone_never_widens_who_the_persons_send_reaches()
    {
        // The person ticked billing; a link's `Everyone=true` must not replace that with every participant.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        using var client = console.CreateClient();
        var forged = new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&Everyone=true", UriKind.Relative);

        var page = await PostDetailAsync(client, forged, new Dictionary<string, string> { ["Says"] = "Add CSV export", ["Targets"] = "billing@test" });

        Assert.DoesNotContain("Nobody was picked", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_a_refused_send_returns_ticks_what_the_person_sent_to_not_what_the_link_named()
    {
        // The page after a send is drawn from the same address as the link. Must fail against a returned page that ticks
        // the agent the link named, with the item's words still in the box, ready for the next Send.
        using var console = Confirmed(Item(1, "Add CSV export", "billing@test", started: false));
        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "billing@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "inventory@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);
        using var client = console.CreateClient();
        var forged = new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&With=inventory%40test", UriKind.Relative);

        var page = await PostDetailAsync(client, forged, new Dictionary<string, string> { ["Says"] = "Add CSV export" });

        Assert.Contains(">Add CSV export</textarea>", page, StringComparison.Ordinal);
        Assert.DoesNotMatch("name=\"Targets\" value=\"inventory@test\" checked", page);
    }

    [Fact]
    public async Task A_removed_items_conversation_opens_with_an_empty_box()
    {
        // It belongs to no item any more, so nothing is offered to send on its behalf.
        var removed = Item(1, "Add CSV export", "billing@test", started: false);
        removed.RemovedRevision = 2;
        using var console = Confirmed(removed);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(">Add CSV export</textarea>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_items_start_link_does_not_carry_its_words()
    {
        // Roadmap text has no bound, and a server refuses a request line past about 8 KB: 3,000 accented characters
        // escape to 18 KB. Walked against the packed tools, such a link was refused with 414 and the item could not be
        // started from the web at all. Must fail against a link that carries the item's text.
        var words = "Exporter " + new string('é', 3000);
        using var console = Confirmed(Item(1, words, "billing@test", started: false));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri(1), TestContext.Current.CancellationToken);
        var links = System.Text.RegularExpressions.Regex.Matches(page, "(?:href|data-src)=\"(/Conversations/Detail[^\"]*)\"");

        Assert.NotEmpty(links);
        Assert.All(links, link => Assert.True(link.Groups[1].Value.Length < 200, link.Groups[1].Value[..Math.Min(200, link.Groups[1].Value.Length)]));
    }
}
