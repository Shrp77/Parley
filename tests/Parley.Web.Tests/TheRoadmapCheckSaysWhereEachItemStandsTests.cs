using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>What the roadmap check sends the agents, and what it tells the person when it cannot send (SPEC.md §5.7).</summary>
/// <remarks>
/// Found by review, lens 3 run I on 19cd7a08: only "waiting on me" was held, so the words for an item in progress, waiting on
/// an agent, blocked, done or closed while blocked, and an item's needs, could each be wrong with every test passing; so
/// could the advice to attach files again when nobody took the words, and the page's word that there is nobody to ask.
/// </remarks>
[Integration]
public sealed class TheRoadmapCheckSaysWhereEachItemStandsTests
{
    private const string Person = "human-user@" + RoadmapConsole.Project;

    private static readonly Uri CheckPage = new($"/Roadmaps/Check?project={RoadmapConsole.Project}&id=1", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RoadmapConsole Console(bool withAgents = true)
    {
        var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        void Item(int number, string text, Wire.ItemFacts facts, params int[] needs)
        {
            var item = new Wire.RoadmapItemDetail
            {
                Id = number, Number = number, Position = number, Text = text, ConversationName = $"rm1-{number}", Facts = facts,
            };

            item.Needs.Add(needs);
            detail.Items.Add(item);
        }

        Item(1, "Add CSV export", new Wire.ItemFacts { ConversationExists = true, OpenExchanges = 1, Collected = true, OpenExchangeIds = { 11 } });
        Item(2, "Add PDF export", new Wire.ItemFacts
        {
            ConversationExists = true, OpenExchanges = 1, Collected = true, OpenExchangeIds = { 12 }, BlockedExchanges = 1, Blocker = "missing_information",
        });
        Item(3, "Pick a currency", new Wire.ItemFacts { ConversationExists = true, BlockedExchanges = 1, Blocker = "needs_decision" });
        Item(4, "Name the files", new Wire.ItemFacts { ConversationExists = true, CompleteExchanges = 1 }, 1, 3);
        Item(5, "Drop XML", new Wire.ItemFacts { ConversationExists = true, BlockedExchanges = 1, Blocker = "conflict", ClosedBy = Person });

        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 5 });
        console.Broker.Seats = true;

        // 11: the person waits on billing, which has collected it. 12: orders waits on billing.
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = "billing@test", ExchangeId = 11 });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@test", WaitedOn = "billing@test", ExchangeId = 12 });

        var rows = new List<(string, string)> { (Person, "parley-console"), ("parley-facilitator@" + RoadmapConsole.Project, "parley-facilitator") };

        if (withAgents)
        {
            rows.Add(("billing@test", "claude-code"));
            rows.Add(("orders@test", "claude-code"));
        }

        foreach (var (name, adapter) in rows)
        {
            console.Broker.Registry.Add(new Wire.RegistryRow
            {
                Name = name, Adapter = adapter, Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            });
        }

        return console;
    }

    private static string TokenIn(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

    private static string BoxIn(string html) =>
        WebUtility.HtmlDecode(Regex.Match(
            html, """<textarea[^>]*name="Says"[^>]*>(.*?)</textarea>""", RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Groups[1].Value);

    [Fact]
    public async Task Each_item_reads_where_it_stands_with_who_holds_it_what_blocks_it_and_what_it_needs()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var offered = BoxIn(await client.GetStringAsync(CheckPage, Ct));

        Assert.Contains("1. Add CSV export - in progress with billing\n", offered, StringComparison.Ordinal);
        Assert.Contains("2. Add PDF export - waiting on billing; blocked: missing information\n", offered, StringComparison.Ordinal);
        Assert.Contains("3. Pick a currency - blocked: needs decision\n", offered, StringComparison.Ordinal);
        Assert.Contains("4. Name the files - needs 1, 3 - done\n", offered, StringComparison.Ordinal);
        Assert.EndsWith("5. Drop XML - closed while blocked: conflict", offered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_attached_to_a_check_nobody_took_are_said_to_need_attaching_again()
    {
        using var console = Console();

        _ = console.Broker.RefusesRequestsTo.Add("billing@test");
        _ = console.Broker.RefusesRequestsTo.Add("orders@test");

        using var client = console.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = TokenIn(await client.GetStringAsync(CheckPage, Ct));

        using var file = new ByteArrayContent("notes"u8.ToArray());
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("My own words"), "Says" },
            { file, "Attachments", "notes.txt" },
        };

        using var posted = await client.PostAsync(CheckPage, form, Ct);
        var html = await posted.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Contains("Nobody took your words, so the files you attached were not placed. Attach them again when you send.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_agent_to_ask_the_page_says_so()
    {
        using var console = Console(withAgents: false);
        using var client = console.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync(CheckPage, Ct);

        Assert.Contains("There is no agent in this project to ask.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_merge_page_names_each_agent_that_answered_once_and_says_how_many_requests_are_still_open()
    {
        // Found by review, lens 3 run I on 19cd7a08: the count of requests still open could read nothing, so the person
        // merged without being told some agents had not answered, and an agent that answered twice was named twice.
        const string Checked = "roadmap-1-check-20261116-1200";

        using var console = Console();

        void Answer(long id, string from, string text)
        {
            console.Broker.ExportMessages.Add(new Wire.Message
            {
                Id = id,
                ExchangeId = id,
                Type = Wire.MessageType.Result,
                From = from,
                To = Person,
                ResponderModel = "test",
                PayloadJson = "{\"outcomes\":[{\"id\":\"request\",\"outcome\":\"answered\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}]}",
                ConversationId = Checked,
                CreatedUtc = "2026-11-16T12:05:00Z",
            });
            console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
            {
                ExchangeId = id, State = Wire.ExchangeState.Closed, OpenedUtc = "2026-11-16T12:00:00Z", ClosedUtc = "2026-11-16T12:05:00Z",
            });
        }

        Answer(21, "billing@test", "Item 1 is done.");
        Answer(22, "orders@test", "Nothing to add.");
        Answer(23, "billing@test", "And item 3 can go.");

        foreach (var open in new long[] { 24, 25 })
        {
            console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
            {
                ExchangeId = open, State = Wire.ExchangeState.Open, OpenedUtc = "2026-11-16T12:00:00Z",
            });
        }

        console.Broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = Checked, Messages = 3, FirstActivityUtc = "2026-11-16T12:00:00Z", LastActivityUtc = "2026-11-16T12:05:00Z",
        });

        using var client = console.CreateClient();

        var page = WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Roadmaps/Merge?project={RoadmapConsole.Project}&id=1&from={Checked}", UriKind.Relative), Ct));

        Assert.Contains("Answered (2): billing@test, orders@test.", page, StringComparison.Ordinal);
        Assert.Contains("2 requests are still open in that conversation", page, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Requested);
    }
}
