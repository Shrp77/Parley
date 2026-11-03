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

        // Only the wait-for graph changes. The transcript draws nothing from it, so the panel alone can move the digest,
        // and a digest of the transcript alone would leave this list stale on screen.
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        var after = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.Contains("<section class=\"waiting-on-you\"", after, StringComparison.Ordinal);
        Assert.NotEqual(Seen(before), Seen(after));
    }

    [Fact]
    public async Task A_message_marked_as_waiting_is_listed_even_when_the_graph_has_no_edge_for_it()
    {
        // Seen on a live store: a message said Waiting on you, was missing from the list, and could not be gone to.
        using var console = new TranscriptConsole();
        console.Broker.Request(9, Orders, Person, "Which ledger?");

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("class=\"reply-to\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"#m9\" target=\"_self\" data-waiting", page, StringComparison.Ordinal);
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
