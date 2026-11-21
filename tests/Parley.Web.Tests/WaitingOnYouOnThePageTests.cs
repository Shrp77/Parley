using Parley.Client.Seating;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the web page lists what is waiting on the person as the terminal's room does: each agent once with a count, and
/// each question a link to where it was asked (SPEC.md §5.6).
/// </summary>
/// <remarks>
/// The page and the room read one derivation, <see cref="WaitingOnYou"/>; these hold the page to drawing it, with links
/// that land on something and with the agent's words escaped.
/// </remarks>
[Integration]
public sealed class WaitingOnYouOnThePageTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Person = "human-user@" + TranscriptConsole.Project;

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static TranscriptConsole Asked()
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, Orders, Person, "Which invoice schema should the export target?");
        console.Broker.Request(2, Orders, Person, "Gross or net? <b>bold</b>");
        console.Broker.Request(3, Billing, Person, "Which currency?");
        console.Broker.Request(4, Orders, Billing, "Not the person's.");

        foreach (var (waiter, exchange) in new[] { (Orders, 1L), (Orders, 2L), (Billing, 3L) })
        {
            console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = waiter, WaitedOn = Person, ExchangeId = exchange });
        }

        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Billing, ExchangeId = 4 });

        return console;
    }

    [Fact]
    public async Task Closing_is_offered_only_on_an_agents_request_and_the_script_reads_the_value_not_that_the_attribute_is_there()
    {
        using var console = Asked();

        // The person's own Request, where the agent asked back: the question waits on them, and only the agent can close it.
        console.Broker.Request(5, Person, Orders, "Export the invoices.");
        console.Broker.Messages.Add(new Wire.Message
        {
            Id = 6,
            ExchangeId = 5,
            Type = Wire.MessageType.Clarify,
            From = Orders,
            To = Person,
            ResponderModel = "test",
            PayloadJson = """{"text":"Which schema?"}""",
            ConversationId = "shared",
            CreatedUtc = "2026-09-16T10:00:00Z",
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 5 });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        // An attribute starting data- is written even when its value is null, so its being there says nothing. That offered
        // to close the person's own Request, which only its receiver can, and the broker refused it.
        Assert.Contains("data-exchange=\"5\" data-agent=\"" + Orders + "\" data-request=\"0\"", page, StringComparison.Ordinal);
        Assert.Contains("data-exchange=\"1\" data-agent=\"" + Orders + "\" data-request=\"1\"", page, StringComparison.Ordinal);
        Assert.Contains(
            "outcome.hidden = button.getAttribute(\"data-request\") !== \"1\";",
            ScriptSource.Code(page, "button.reply-to"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("hasAttribute(\"data-request\")", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_waiting_on_the_person_can_be_answered_or_closed_where_it_was_asked()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        // On the message, so the person stays in the conversation. Three questions to the person; the fourth is not theirs.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(page, "class=\"reply-to\""));
        Assert.Contains("data-exchange=\"1\" data-agent=\"" + Orders + "\" data-request=\"1\"", page, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(page, "class=\"close-request quiet\""));
        Assert.Contains("class=\"close-request quiet\" data-exchange=\"3\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"close-request quiet\" data-exchange=\"4\"", page, StringComparison.Ordinal);

        // A form in the followed region would carry a new antiforgery token at each render, change the region's digest
        // and have it replaced every few seconds, which moves the reader.
        var followed = page[page.IndexOf("id=\"followed\"", StringComparison.Ordinal)..page.IndexOf("id=\"composer\"", StringComparison.Ordinal)];

        Assert.DoesNotContain("__RequestVerificationToken", followed, StringComparison.Ordinal);

        // A way to go from one to the next, and the outcomes a reply can close a Request with.
        Assert.Contains("id=\"next-waiting\"", page, StringComparison.Ordinal);
        Assert.Contains("data-waiting", Section(page), StringComparison.Ordinal);
        Assert.Contains("id=\"reply-outcome\"", page, StringComparison.Ordinal);
        Assert.Contains("Close it: Answered", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_message_to_the_person_in_a_turn_sent_to_several_keeps_its_anchor()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(1, Orders, Billing, "Everyone: which rate?");
        console.Broker.Request(2, Orders, Person, "Everyone: which rate?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 2 });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("id=\"m2\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"#m2\"", Section(page), StringComparison.Ordinal);
        Assert.Contains("data-exchange=\"2\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_agent_is_listed_once_with_its_count_and_each_question_links_to_its_message()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var list = Section(page);

        Assert.Contains("Waiting on you", list, StringComparison.Ordinal);
        Assert.Contains($"<strong>{Orders}</strong> (2)", list, StringComparison.Ordinal);
        Assert.Contains($"<strong>{Billing}</strong> (1)", list, StringComparison.Ordinal);

        // Each link targets the message's own anchor, which the page draws.
        foreach (var id in new[] { 1, 2, 3 })
        {
            Assert.Contains($"href=\"#m{id}\"", list, StringComparison.Ordinal);
            Assert.Contains($"id=\"m{id}\"", page, StringComparison.Ordinal);
        }

        // Not a question to the person, so not listed. (The timeline below links every message, so this reads the list.)
        Assert.DoesNotContain("href=\"#m4\"", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inbox_lists_what_is_waiting_on_the_person_from_the_broker()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("Which invoice schema should the export target?", page, StringComparison.Ordinal);
        Assert.Contains("Which currency?", page, StringComparison.Ordinal);
        Assert.Contains("Owes a result", page, StringComparison.Ordinal);

        // An exchange the person is not the one owing is not theirs to answer.
        Assert.DoesNotContain("Not the person", page, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", page, StringComparison.Ordinal);
    }

    private static string Section(string page)
    {
        var start = page.IndexOf("<section class=\"waiting-on-you\"", StringComparison.Ordinal);

        Assert.True(start >= 0, "no waiting-on-you section");

        return page[start..page.IndexOf("</section>", start, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task An_agents_words_in_the_list_are_escaped()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<b>bold</b>", page, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_waiting_draws_no_list()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("class=\"waiting-on-you\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_waits_on_the_person_is_read_from_summaries_and_never_a_whole_conversation()
    {
        // Review, quality lens (0.62.7): every conversation with an open exchange was exported whole as soon as one agent
        // waited, on every Inbox open and every poll of a conversation page.
        using var console = Asked();
        console.Broker.AlsoListed.Add(new Wire.ConversationSummary { ConversationId = "plain-work", HasOpenExchange = true });
        console.Broker.AlsoListed.Add(new Wire.ConversationSummary { ConversationId = "more-plain-work", HasOpenExchange = true });

        using var client = console.CreateClient();

        _ = await client.GetStringAsync(
            new Uri($"/Inbox?project={TranscriptConsole.Project}", UriKind.Relative), TestContext.Current.CancellationToken);

        // Never a whole conversation: a question needs only the agent's newest message in its exchange.
        Assert.DoesNotContain(console.Broker.ExportsAsked, e => !e.LatestOnly);
    }
}
