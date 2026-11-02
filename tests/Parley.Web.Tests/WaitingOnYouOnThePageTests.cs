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
}
