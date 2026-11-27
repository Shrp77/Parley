using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>A reader can see which exchange each message belongs to, and read one exchange alone.</summary>
[Integration]
public sealed class ThreadsOnThePageTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Person = "human-user@" + TranscriptConsole.Project;

    private static readonly Uri PageUri = new($"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static void Reply(TranscriptConsole console, long id, long exchange, string from, string to, string text) =>
        console.Broker.Messages.Add(new Wire.Message
        {
            Id = id,
            ExchangeId = exchange,
            Type = Wire.MessageType.Clarify,
            From = from,
            To = to,
            ResponderModel = "test",
            PayloadJson = "{\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}",
            ConversationId = "shared",
            CreatedUtc = "2026-09-16T10:00:00Z",
        });

    private static async Task<string> PageAsync(TranscriptConsole console)
    {
        using var client = console.CreateClient();

        return await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
    }

    private static string Entry(string page, long id)
    {
        var at = page.IndexOf($"id=\"m{id}\"", StringComparison.Ordinal);

        Assert.True(at > 0, $"no message {id}");

        return page[at..page.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    private static TranscriptConsole Interleaved()
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, Person, Orders, "Gross or net on the September invoices?");
        console.Broker.Request(2, Person, Billing, "Something else entirely.");
        Reply(console, 3, 1, Orders, Person, "Net, unless you say otherwise.");
        Reply(console, 4, 1, Person, Orders, "Net it is.");

        return console;
    }

    [Fact]
    public async Task What_opened_an_exchange_stands_out_and_what_continues_it_sits_in()
    {
        using var console = Interleaved();

        var page = await PageAsync(console);

        Assert.DoesNotContain("in-thread", Entry(page, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("in-thread", Entry(page, 2), StringComparison.Ordinal);
        Assert.Contains("in-thread", Entry(page, 3), StringComparison.Ordinal);
        Assert.Contains("in-thread", Entry(page, 4), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_order_things_happened_in_is_kept()
    {
        using var console = Interleaved();

        var page = await PageAsync(console);
        var at = new[] { 1, 2, 3, 4 }.Select(id => page.IndexOf($"id=\"m{id}\"", StringComparison.Ordinal)).ToArray();

        Assert.Equal(at.Order(), at);
    }

    [Fact]
    public async Task A_message_says_what_it_continues_only_when_something_else_came_between()
    {
        using var console = Interleaved();

        var page = await PageAsync(console);

        Assert.Contains("class=\"thread-of\" href=\"#m1\"", Entry(page, 3), StringComparison.Ordinal);
        Assert.Contains("Gross or net on the September invoices?", Entry(page, 3), StringComparison.Ordinal);
        Assert.DoesNotContain("thread-of", Entry(page, 4), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_opening_is_shortened_to_one_line_and_cannot_carry_markup()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(1, Person, Orders, "<b>bold</b>\n\nline two " + new string('x', 200));
        console.Broker.Request(2, Person, Billing, "Other.");
        Reply(console, 3, 1, Orders, Person, "Yes.");

        var entry = Entry(await PageAsync(console), 3);
        var said = Regex.Match(entry, "class=\"thread-of\".*?</a>", RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Value;

        Assert.Contains("&lt;b&gt;bold&lt;/b&gt; line two", said, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", said, StringComparison.Ordinal);
        Assert.Contains("&#x2026;", said, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_message_can_ask_for_its_thread_when_it_has_one()
    {
        using var console = Interleaved();

        var page = await PageAsync(console);

        Assert.Contains("data-exchanges=\"1\" data-label=\"Thread (3)\"", Entry(page, 1), StringComparison.Ordinal);
        Assert.Contains("data-exchanges=\"1\" data-label=\"Thread (3)\"", Entry(page, 4), StringComparison.Ordinal);
        Assert.DoesNotContain("thread-focus", Entry(page, 2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_to_several_is_one_entry_carrying_every_exchange()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(1, Person, Orders, "Same words.");
        console.Broker.Request(2, Person, Billing, "Same words.");
        Reply(console, 3, 2, Billing, Person, "Yes.");

        var page = await PageAsync(console);

        var entry = Entry(page, 1);

        // On the row, which is what a thread's filter reads, as well as on its button.
        Assert.Contains("data-exchanges=\"1 2\"", entry[..entry.IndexOf("class=\"turn", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("data-exchanges=\"1 2\" data-label=\"Thread (3)\"", entry, StringComparison.Ordinal);
    }
}
