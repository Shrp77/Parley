using System.Net;
using System.Text.RegularExpressions;
using Parley.Client.Rendering;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>That the web console's two new pages do what they describe, and only on a POST (SPEC.md §5.0).</summary>
[Integration]
public sealed class SpinOffAndMovePagesTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Project = TranscriptConsole.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri At(string path) => new(path, UriKind.Relative);

    private static string Spin(string more = "") => $"/Conversations/SpinOff?project={Project}&id=shared{more}";

    private static string Move(string more = "") => $"/Conversations/Move?project={Project}&id=shared{more}";

    private static TranscriptConsole Console()
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, Orders, Billing, "The disk is <b>full</b>.\nFix it.");
        console.Broker.Request(2, Billing, Orders, "Which disk?");
        console.Broker.Registry.Add(new Wire.RegistryRow { Name = Orders });
        console.Broker.Registry.Add(new Wire.RegistryRow { Name = Billing });

        return console;
    }

    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(
        html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value);

    private static async Task<(HttpResponseMessage Response, string Body)> PostAsync(
        HttpClient client, string path, params (string Name, string Value)[] fields)
    {
        var form = await client.GetStringAsync(At(path), Ct);
        var content = new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Token(form)), .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))]);
        var response = await client.PostAsync(At(path), content, Ct);

        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private static HttpClient NoRedirects(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> console) =>
        console.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task A_spin_off_opens_with_the_message_quoted_only_its_author_ticked_and_nothing_done()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Spin("&from=1")), Ct);

        Assert.Contains("orders@test said in shared:", page, StringComparison.Ordinal);
        Assert.Contains("&gt; The disk is &lt;b&gt;full&lt;/b&gt;.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>full", page, StringComparison.Ordinal);
        Assert.Matches("""name="Targets" value="orders@test" checked""", page);
        Assert.DoesNotMatch("""name="Targets" value="billing@test" checked""", page);
        Assert.Empty(console.Broker.SpinOffs);
    }

    [Fact]
    public async Task A_link_cannot_fill_the_box_or_tick_anybody()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Spin("&from=1&Says=evil-words&Targets=billing@test&Name=evil-name")), Ct);

        Assert.DoesNotContain("evil-words", page, StringComparison.Ordinal);
        Assert.DoesNotContain("evil-name", page, StringComparison.Ordinal);
        Assert.DoesNotMatch("""name="Targets" value="billing@test" checked""", page);
    }

    [Fact]
    public async Task A_message_that_is_not_there_opens_an_empty_box_and_says_so()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Spin("&from=99")), Ct);

        Assert.Contains("not in this conversation any more", page, StringComparison.Ordinal);
        Assert.DoesNotMatch("""name="Targets" value="[^"]*" checked""", page);
    }

    [Fact]
    public async Task A_spin_off_with_no_message_opens_empty()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Spin()), Ct);

        Assert.DoesNotContain("said in shared", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_that_is_missing_something_sends_nothing_and_keeps_what_was_written()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var (_, body) = await PostAsync(client, Spin(), ("Name", "disk-cleanup"), ("Says", "my own words"));

        Assert.Contains("Nobody was picked", body, StringComparison.Ordinal);
        Assert.Contains("my own words", body, StringComparison.Ordinal);
        Assert.Contains("value=\"disk-cleanup\"", body, StringComparison.Ordinal);
        Assert.Empty(console.Broker.SpinOffs);
    }

    [Fact]
    public async Task Every_message_leads_to_a_spin_off_and_every_conversation_to_a_move()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Detail?project={Project}&id=shared"), Ct);

        Assert.Equal(2, Regex.Matches(page, "class=\"spin-off\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Count);
        Assert.Contains("from=2", page, StringComparison.Ordinal);
        Assert.Contains("id=\"move-start\"", page, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(page, "class=\"move-pick\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Count);
    }

    private static RoadmapConsole Seated()
    {
        var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "human-user@" + RoadmapConsole.Project, Adapter = "parley-console", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });

        foreach (var agent in new[] { Orders, Billing })
        {
            console.Broker.Registry.Add(new Wire.RegistryRow { Name = agent, Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active });
        }

        return console;
    }

    private static (string, string)[] Spinning(params string[] to) =>
        [("Name", "disk-cleanup"), ("Says", "my own words"), .. to.Select(agent => ("Targets", agent))];

    [Fact]
    public async Task A_send_links_first_then_sends_and_goes_to_the_new_conversation()
    {
        using var console = Seated();
        using var client = NoRedirects(console);

        var (response, _) = await PostAsync(client, Spin(), Spinning(Orders));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("id=disk-cleanup", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var link = Assert.Single(console.Broker.SpunOff);

        Assert.Equal(("shared", "disk-cleanup"), (link.Parent, link.Child));

        var sent = Assert.Single(console.Broker.Requested);

        Assert.Equal(("disk-cleanup", Orders), (sent.ConversationId, sent.To));
        Assert.Contains("my own words", sent.PayloadJson, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Abandoned);
    }

    [Fact]
    public async Task A_link_refused_sends_nothing_and_keeps_everything()
    {
        using var console = Seated();
        console.Broker.SpinOffRefusal = "There is already a conversation called disk-cleanup.";
        using var client = console.CreateClient();

        var (_, body) = await PostAsync(client, Spin(), Spinning(Orders));

        Assert.Contains("Nothing was sent: There is already", body, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Requested);
        Assert.Contains("my own words", body, StringComparison.Ordinal);
        Assert.Contains("value=\"disk-cleanup\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_send_that_reached_nobody_takes_the_link_back_and_keeps_everything()
    {
        using var console = Seated();
        console.Broker.RefusesRequestsTo.Add(Orders);
        using var client = console.CreateClient();

        var (_, body) = await PostAsync(client, Spin(), Spinning(Orders));

        Assert.Equal("disk-cleanup", Assert.Single(console.Broker.Abandoned).Child);
        Assert.Contains("my own words", body, StringComparison.Ordinal);
        Assert.Contains("value=\"disk-cleanup\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_send_that_reached_anybody_keeps_the_link_and_goes_there_rather_than_offering_a_retry_it_would_refuse()
    {
        using var console = Seated();
        console.Broker.RefusesRequestsTo.Add(Billing);
        using var client = NoRedirects(console);

        var (response, _) = await PostAsync(client, Spin(), Spinning(Orders, Billing));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(console.Broker.Abandoned);
    }

    [Fact]
    public async Task A_send_to_several_goes_together_and_the_page_says_why_it_grew()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Same words.");
        console.Broker.Request(2, Orders, "inventory@test", "Same words.");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move("&x=1")), Ct);

        Assert.Contains("name=\"Exchanges\" value=\"1\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Exchanges\" value=\"2\"", page, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode(Parley.Client.Rendering.MovePicking.SentTogether), page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_export_carries_links_moves_and_settled_pairs()
    {
        var export = new Parley.Core.ConversationExport(
            [],
            [],
            Links: new Parley.Core.ConversationLinks("logging", Parley.Core.ConversationLinkKind.Move, ["child-one"]),
            Moves: [new Parley.Core.MoveRecord(1, "a", "b", "human-user@test", DateTimeOffset.UnixEpoch, 5, 3, [7])],
            SettledElsewhere: [new Parley.Core.SettledElsewhere(3, 4, "other")]);

        var file = System.Text.Encoding.UTF8.GetString(Parley.Web.Pages.Conversations.DetailModel.RawExport("shared", export));

        foreach (var expected in new[] { "logging", "child-one", "destination", "settledElsewhere", "other" })
        {
            Assert.Contains(expected, file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_move_page_lists_what_goes_and_changes_nothing_until_the_post()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move("&x=1")), Ct);

        Assert.Contains("This will move 1 exchange", page, StringComparison.Ordinal);
        Assert.Contains("is still in progress", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Exchanges\" value=\"1\"", page, StringComparison.Ordinal);
        Assert.Contains("deck.md", page, StringComparison.Ordinal);
        Assert.Contains("You can move them back afterwards, unless shared has closed.", page, StringComparison.Ordinal);
        Assert.Empty(console.Broker.MovesAsked);
    }

    [Fact]
    public async Task With_nothing_picked_it_offers_everything_to_pick_from_and_cannot_move()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move()), Ct);

        Assert.Equal(2, Regex.Matches(page, "name=\"x\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Count);
        Assert.DoesNotContain("method=\"post\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_moves_what_it_carried_and_goes_to_the_destination()
    {
        using var console = Console();
        using var client = NoRedirects(console);

        var (response, _) = await PostAsync(client, Move("&x=1"), ("Exchanges", "1"), ("Where", "new"), ("NewName", "disk-cleanup"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("id=disk-cleanup", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var asked = Assert.Single(console.Broker.MovesAsked);

        Assert.Equal(("shared", "disk-cleanup", true), (asked.Source, asked.Destination, asked.NewConversation));
        Assert.Equal([1L], asked.Exchanges);
    }

    [Fact]
    public async Task What_the_address_picked_does_not_decide_what_a_post_moves()
    {
        using var console = Console();
        using var client = NoRedirects(console);

        _ = await PostAsync(client, Move("&x=1&x=2"), ("Exchanges", "2"), ("Where", "new"), ("NewName", "disk-cleanup"));

        Assert.Equal([2L], Assert.Single(console.Broker.MovesAsked).Exchanges);
    }

    [Fact]
    public async Task A_refusal_says_why_and_keeps_what_was_chosen()
    {
        using var console = Console();
        console.Broker.MoveRefusal = "disk-cleanup is closed, and a closed conversation stays closed.";
        using var client = console.CreateClient();

        var (_, body) = await PostAsync(client, Move("&x=1"), ("Exchanges", "1"), ("Where", "new"), ("NewName", "disk-cleanup"));

        Assert.Contains("Nothing moved:", body, StringComparison.Ordinal);
        Assert.Contains("stays closed", body, StringComparison.Ordinal);
        Assert.Contains("value=\"disk-cleanup\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_destination_named_moves_nothing()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var (_, body) = await PostAsync(client, Move("&x=1"), ("Exchanges", "1"), ("Where", "new"), ("NewName", "  "));

        Assert.Contains("It needs a name", body, StringComparison.Ordinal);
        Assert.Empty(console.Broker.MovesAsked);
    }

    [Fact]
    public async Task The_transcript_says_what_moved_where_the_entries_were_and_the_panel_says_who_is_related()
    {
        using var console = Console();
        console.Broker.Moves.Add(new Wire.MoveRecord
        {
            Id = 1, Source = "shared", Destination = "disk-cleanup", MovedBy = "human-user@alpha", MovedUtc = "2026-09-16T10:00:00Z", FirstMessage = 2, Messages = 3,
            Exchanges = { 7 },
        });
        console.Broker.Parent = "logging";
        console.Broker.Children.Add("child-one");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Detail?project={Project}&id=shared"), Ct);
        var note = page.IndexOf("moved from here to disk-cleanup", StringComparison.Ordinal);

        Assert.InRange(note, page.IndexOf("id=\"m1\"", StringComparison.Ordinal), page.IndexOf("id=\"m2\"", StringComparison.Ordinal));
        Assert.Contains("Spun off from", page, StringComparison.Ordinal);
        Assert.Contains("id=logging", page, StringComparison.Ordinal);
        Assert.Contains("child-one", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_with_everything_moved_out_says_where_it_went()
    {
        using var console = new TranscriptConsole();
        console.Broker.Moves.Add(new Wire.MoveRecord
        {
            Id = 1, Source = "shared", Destination = "disk-cleanup", MovedBy = "human-user@alpha", MovedUtc = "2026-09-16T10:00:00Z", Messages = 3,
            Exchanges = { 7 },
        });
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Detail?project={Project}&id=shared"), Ct);

        Assert.Contains("moved from here to disk-cleanup", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was recorded", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_says_what_a_conversation_was_spun_off_from_apart_from_its_name()
    {
        using var console = Console();
        console.Broker.Parent = "logging";
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Projects/Agents?project={Project}"), Ct);

        Assert.Contains(
            $"shared<span class=\"spinoff-of\">{WebUtility.HtmlEncode(ConversationWording.SpinoffSuffix("logging"))}</span>",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_destroy_page_says_what_a_conversation_was_spun_off_from()
    {
        using var console = Console();
        console.Broker.Parent = "logging";
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Destroy?project={Project}&id=shared"), Ct);

        Assert.Contains($"<li>{WebUtility.HtmlEncode(ConversationWording.Listed("shared", "logging"))}</li>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_destination_is_chosen_by_its_name_alone_though_its_row_says_more()
    {
        using var console = Console();
        console.Broker.Parent = "logging";
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Move?project={Project}&id=other&x=1"), Ct);

        Assert.Matches(
            $"<option value=\"shared\"[^>]*>{Regex.Escape(WebUtility.HtmlEncode(ConversationWording.Listed("shared", "logging")))}</option>",
            page);
    }

    [Fact]
    public async Task A_list_to_choose_a_destination_from_says_what_each_was_spun_off_from_and_reads_every_page()
    {
        using var console = Console();
        console.Broker.Parent = "logging";
        console.Broker.TwoPages = true;
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Move?project={Project}&id=other&x=1"), Ct);

        Assert.Contains($"value=\"shared\"", page, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode(ConversationWording.Listed("shared", "logging")), page, StringComparison.Ordinal);
        Assert.Contains("value=\"second-page\"", page, StringComparison.Ordinal);
        Assert.All(console.Broker.Listings, asked => Assert.True(asked.IncludeArchived));
    }

    [Fact]
    public async Task The_conversation_on_screen_is_not_offered_as_where_its_entries_go()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move("&x=1")), Ct);

        Assert.DoesNotContain("<option value=\"shared\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_split_settled_pair_is_said_on_the_page()
    {
        using var console = Console();
        console.Broker.SettledElsewhere.Add(new Wire.SettledElsewhere { ExchangeId = 3, SettledBy = 4, ConversationId = "other" });
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Detail?project={Project}&id=shared"), Ct);

        Assert.Contains("One of them is now in other", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panels_move_link_goes_to_the_move_page_for_this_project_and_conversation()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Conversations/Detail?project={Project}&id=shared"), Ct);
        var link = Regex.Match(page, """<a [^>]*id="move-start"[^>]*>""", RegexOptions.None, TimeSpan.FromSeconds(2));

        Assert.True(link.Success);

        var href = WebUtility.HtmlDecode(Regex.Match(link.Value, """href="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value);

        Assert.Equal(Move(), href);
        Assert.Contains("Move entries out of shared", await client.GetStringAsync(At(href), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_was_picked_has_no_note_and_what_was_added_says_why()
    {
        // FR-009. The row picked says nothing, because nobody needs telling why what they chose is going.
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Same words.");
        console.Broker.Request(2, Orders, "inventory@test", "Same words.");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move("&x=1")), Ct);
        var rows = Regex.Matches(page, "<li>.*?</li>", RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Select(row => row.Value).Where(row => row.Contains("the first words", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain("Added:", rows[0], StringComparison.Ordinal);
        Assert.Contains("Added:", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_to_an_existing_conversation_moves_there_and_starts_nothing_new()
    {
        using var console = Console();
        using var client = NoRedirects(console);

        var (response, _) = await PostAsync(client, Move("&x=1"), ("Exchanges", "1"), ("Where", "existing"), ("Existing", "other-work"), ("NewName", "not-this"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("id=other-work", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var asked = Assert.Single(console.Broker.MovesAsked);

        Assert.Equal(("shared", "other-work", false), (asked.Source, asked.Destination, asked.NewConversation));
    }

    [Fact]
    public async Task An_existing_conversation_not_chosen_moves_nothing()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var (_, body) = await PostAsync(client, Move("&x=1"), ("Exchanges", "1"), ("Where", "existing"), ("Existing", ""), ("NewName", "not-this"));

        Assert.Contains("No conversation was chosen", body, StringComparison.Ordinal);
        Assert.Empty(console.Broker.MovesAsked);
    }

    [Fact]
    public async Task A_picked_exchange_that_has_since_moved_says_so_and_offers_no_way_to_move()
    {
        using var console = Console();
        console.Broker.PreviewRefusal = "Exchange 1 is not in shared. It may have moved already, so look at the conversation again.";
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move("&x=1")), Ct);

        Assert.Contains("It may have moved already", page, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\" class=\"move-form\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("This will move", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_move_page_says_a_follow_up_stays_behind_unless_it_is_picked()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move("&x=1")), Ct);

        Assert.Contains("stays behind unless you pick it too", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Exchanges\" value=\"1\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Exchanges\" value=\"2\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roadmap_items_conversation_and_an_ownership_conversation_are_not_offered_as_destinations()
    {
        using var console = Console();
        console.Broker.AlsoListed.Add(new Wire.ConversationSummary { ConversationId = "plain-work", HasOpenExchange = true });
        console.Broker.AlsoListed.Add(new Wire.ConversationSummary { ConversationId = "owns-billing", HasOpenExchange = true });
        console.Broker.AlsoListed.Add(new Wire.ConversationSummary
        {
            ConversationId = "work-item", HasOpenExchange = true, Item = new Wire.RoadmapItemRef { ItemNumber = 1 },
        });
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At(Move("&x=1")), Ct);

        // The first row is the one the filters leave, so the list is known to be read.
        Assert.Contains("<option value=\"plain-work\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"owns-billing\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"work-item\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_with_no_parent_lists_as_its_name_alone()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(At($"/Projects/Agents?project={Project}"), Ct);

        Assert.DoesNotContain("spinoff from", page, StringComparison.Ordinal);
    }
}
