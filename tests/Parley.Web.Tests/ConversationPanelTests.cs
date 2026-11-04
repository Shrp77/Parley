using System.Net;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page's panel keeps the name, find, the filter and what is waiting within reach, and that what is
/// waiting is drawn above the tree of who to write to, which can be taller than the window.
/// </summary>
[Integration]
public sealed class ConversationPanelTests
{
    private const string Orders = "orders@test";
    private const string Person = "human-user@" + TranscriptConsole.Project;

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static readonly Uri FollowUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared&handler=Follow", UriKind.Relative);

    private static TranscriptConsole Asked()
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, Orders, Person, "Gross or net? <b>bold</b>");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        return console;
    }

    private static Uri Elsewhere(string conversation) => new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id={conversation}&handler=WaitingElsewhere", UriKind.Relative);

    [Fact]
    public async Task The_name_find_and_the_filter_are_in_the_panel_and_outside_what_following_replaces()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        var panel = page[page.IndexOf("id=\"convo-panel\"", StringComparison.Ordinal)..page.IndexOf("id=\"followed-side\"", StringComparison.Ordinal)];

        Assert.Contains("<h1>shared</h1>", panel, StringComparison.Ordinal);
        Assert.Contains("id=\"find\"", panel, StringComparison.Ordinal);
        Assert.Contains("id=\"filter\"", panel, StringComparison.Ordinal);

        // Replaced every few seconds, these would lose what the person had typed and where their caret was.
        var follow = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("id=\"find\"", follow, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"filter\"", follow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_is_waiting_is_drawn_above_who_to_write_to()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var waiting = page.IndexOf("<section class=\"waiting-on-you\"", StringComparison.Ordinal);
        var tree = page.IndexOf("<aside class=\"recipients\"", StringComparison.Ordinal);

        Assert.True(waiting >= 0 && tree > waiting, "what is waiting is not above the tree");
        Assert.True(waiting < page.IndexOf("id=\"followed\"", StringComparison.Ordinal), "what is waiting is not in the panel");
    }

    [Fact]
    public async Task A_follow_carries_the_panels_changing_half_and_a_change_there_alone_moves_the_digest()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, "billing@test", "Gross or net?");

        using var client = console.CreateClient();

        var before = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.Contains("id=\"followed-side\"", before, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting-on-you", before, StringComparison.Ordinal);

        // Only the wait-for graph changes, on a message to somebody else that no mark can sit on, so the panel alone moves the digest,
        // and a digest of the transcript alone would leave this list stale on screen.
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        var after = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.Contains("<section class=\"waiting-on-you\"", after, StringComparison.Ordinal);
        Assert.NotEqual(Seen(before), Seen(after));
    }

    [Fact]
    public async Task A_question_handed_back_is_neither_marked_nor_listed()
    {
        // An agent settled its question and the turn came back to it (SPEC.md 5.6). That writes no message, so the transcript
        // still ends with its question, and only the wait-for graph says the person owes nothing. Seen on a live store.
        using var console = new TranscriptConsole();
        console.Broker.Request(9, Orders, Person, "Which ledger?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = Orders, ExchangeId = 9 });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("class=\"reply-to\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("target=\"_self\" data-waiting", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mark_and_the_list_always_name_the_same_messages()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "class=\"reply-to\""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "href=\"#m1\" target=\"_self\" data-waiting"));
    }

    [Fact]
    public async Task A_question_handed_back_stays_unmarked_while_another_question_waits()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(9, Orders, Person, "Which ledger?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = Orders, ExchangeId = 9 });
        console.Broker.Request(10, Orders, Person, "And which year?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 10 });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "class=\"reply-to\""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "href=\"#m10\" target=\"_self\" data-waiting"));
        Assert.DoesNotContain("href=\"#m9\" target=\"_self\" data-waiting", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_earlier_question_on_the_same_exchange_is_neither_marked_nor_listed()
    {
        // The agent asked, the person replied, the agent asked again: only its latest is owed.
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Person, Orders, "Close the ledger.");
        console.Broker.Messages.Add(Said(2, Orders, Person, "Gross or net?"));
        console.Broker.Messages.Add(Said(3, Person, Orders, "Net."));
        console.Broker.Messages.Add(Said(4, Orders, Person, "Which month?"));
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "class=\"reply-to\""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "target=\"_self\" data-waiting"));
        Assert.Contains("href=\"#m4\" target=\"_self\" data-waiting", page, StringComparison.Ordinal);
        Assert.True(
            page.IndexOf("class=\"reply-to\"", StringComparison.Ordinal) > page.IndexOf("Net.", StringComparison.Ordinal),
            "the reply box is on an earlier question");

        static Wire.Message Said(long id, string from, string to, string words) => new()
        {
            Id = id,
            ExchangeId = 1,
            Type = Wire.MessageType.Clarify,
            From = from,
            To = to,
            ResponderModel = "test",
            PayloadJson = "{\"text\":" + System.Text.Json.JsonSerializer.Serialize(words) + "}",
            ConversationId = "shared",
            CreatedUtc = "2026-09-16T10:00:00Z"
        };
    }

    [Fact]
    public async Task Each_question_is_listed_once_under_its_agent()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(1, Orders, Person, "Gross or net?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });
        console.Broker.Request(2, Orders, Person, "Which ledger?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 2 });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "href=\"#m1\" target=\"_self\" data-waiting"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "href=\"#m2\" target=\"_self\" data-waiting"));
        Assert.Contains("<strong>orders@test</strong> (2)", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_conversations_list_counts_only_what_waits_on_the_person()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(1, Orders, "billing@test", "Not the person's.");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = "billing@test", ExchangeId = 1 });

        using var client = console.CreateClient();

        var list = await client.GetStringAsync(Elsewhere("other"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("data-elsewhere", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_conversations_list_says_how_many_wait_on_the_person_in_all_and_in_each()
    {
        using var console = Asked();

        console.Broker.Request(2, Orders, Person, "And which market?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 2 });
        console.Broker.Request(3, Orders, "billing@test", "Not the person's.");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = "billing@test", ExchangeId = 3 });

        using var client = console.CreateClient();

        var list = await client.GetStringAsync(Elsewhere("other"), TestContext.Current.CancellationToken);

        Assert.Contains("<span class=\"waiting-count\">2</span>", list, StringComparison.Ordinal);
        Assert.Contains("<strong>shared</strong> (2)", list, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(list, "data-elsewhere"));
    }

    [Fact]
    public async Task The_page_has_the_place_the_other_conversations_list_arrives_in()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("<div id=\"elsewhere\"></div>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_conversations_list_reads_every_page_archived_ones_included()
    {
        using var console = Asked();
        console.Broker.TwoPages = true;
        using var client = console.CreateClient();

        _ = await client.GetStringAsync(Elsewhere("other"), TestContext.Current.CancellationToken);

        Assert.Equal(2, console.Broker.Listings.Count);
        Assert.All(console.Broker.Listings, asked => Assert.True(asked.IncludeArchived));
    }

    [Fact]
    public async Task The_other_conversations_list_names_each_with_a_link_to_the_message_and_encodes_what_was_said()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        // The scripted broker's messages are all in "shared", so a page open on another conversation sees them as elsewhere.
        var list = await client.GetStringAsync(Elsewhere("other"), TestContext.Current.CancellationToken);

        Assert.Contains("<strong>shared</strong> (1)", list, StringComparison.Ordinal);
        Assert.Contains("id=shared#m1", list, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bold", list, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;bold", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_conversations_list_leaves_out_the_one_on_screen()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var list = await client.GetStringAsync(Elsewhere("shared"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("data-elsewhere", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_conversations_list_has_no_frame_so_it_keeps_no_seat()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var list = await client.GetStringAsync(Elsewhere("other"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<header", list, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_other_conversations_list_of_a_project_this_console_does_not_know_is_not_found()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/Conversations/Detail?project=nowhere&id=shared&handler=WaitingElsewhere", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Seen(string html)
    {
        var at = html.IndexOf("data-seen=\"", StringComparison.Ordinal);

        Assert.True(at >= 0, "the region carries no digest");

        return html.Substring(at + "data-seen=\"".Length, 64);
    }
}
