using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The person is told, on the conversation page, which agents read an exchange and what the project's agents searched the
/// history for (SPEC.md §5.8).
/// </summary>
/// <remarks>
/// The page draws what the export carries, which only the operator reads, so this is the person's view and no agent's. A
/// read is named beneath the exchange's first message and never under a reply; a search, which belongs to no exchange,
/// is listed beside the conversation with its words and the number of matches.
/// </remarks>
[Integration]
public sealed class HistoryReadsAreShownTests
{
    private const string Orders = "orders@test";
    private const string Person = "human-user@" + TranscriptConsole.Project;

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TranscriptConsole Asked()
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, Person, Orders, "Gross or net?");
        console.Broker.Reads.Add(new Wire.HistoryReadRow
        {
            Reader = "inventory@test",
            ExchangeId = 1,
            CreatedUtc = "2026-11-17T09:30:00.0000000+00:00",
        });
        console.Broker.Reads.Add(new Wire.HistoryReadRow
        {
            Reader = "billing@test",
            Words = "price gross",
            Matches = 2,
            CreatedUtc = "2026-11-17T09:31:00.0000000+00:00",
        });

        return console;
    }

    [Fact]
    public async Task The_exchange_says_who_read_it()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, Ct);

        Assert.Contains("read by inventory@test at ", page, StringComparison.Ordinal);
        Assert.Equal(1, page.Split("read by inventory@test").Length - 1);
    }

    [Fact]
    public async Task A_search_is_listed_with_its_words_and_how_many_matched()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, Ct);

        Assert.Contains("Searched the history", page, StringComparison.Ordinal);
        Assert.Contains("billing@test: <q>price gross</q>, 2 matches", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_reads_neither_is_drawn()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Person, Orders, "Gross or net?");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, Ct);

        Assert.DoesNotContain("read by ", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Searched the history", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Words_an_agent_searched_for_are_drawn_as_text_and_never_as_markup()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Person, Orders, "Gross or net?");
        console.Broker.Reads.Add(new Wire.HistoryReadRow
        {
            Reader = "billing@test",
            Words = "<script>alert(1)</script>",
            Matches = 0,
            CreatedUtc = "2026-11-17T09:31:00.0000000+00:00",
        });
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, Ct);

        Assert.DoesNotContain("<script>alert(1)", page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page, StringComparison.Ordinal);
    }
}
