using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// An agent reading the project's past over the wire (SPEC.md §5.8, §10.1): any exchange by number, a search, framed as a
/// record, changing nothing, and shown to the person.
/// </summary>
/// <remarks>
/// A third agent reads what two others said to each other, which no operation allowed before 2026-11-17. The guards that
/// matter are that the reader's inbox is not a source for it, that what the receiver is owed is untouched, and that the
/// person's own read of the conversation carries a record of who read.
/// </remarks>
[Integration]
public sealed class ReadingHistoryTests
{
    private const string Orders = "orders@m1";
    private const string Billing = "billing@m1";
    private const string Inventory = "inventory@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LoopbackBroker Broker, AttachedClient Orders, AttachedClient Billing, AttachedClient Inventory, long Exchange)> SetUpAsync()
    {
        var broker = await LoopbackBroker.StartAsync(Ct);
        var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        var opened = await orders.Client.RequestAsync(Requests.Open(Orders, Billing, "the invoice total is gross of tax"), cancellationToken: Ct);

        return (broker, orders, billing, inventory, opened.Outcomes.Single().ExchangeId);
    }

    [Fact]
    public async Task An_agent_that_took_no_part_reads_the_exchange_by_number_and_finds_it_framed()
    {
        var (broker, orders, billing, inventory, exchange) = await SetUpAsync();
        await using var _b = broker;
        await using var _o = orders;
        await using var _i = billing;
        await using var _n = inventory;

        var read = await inventory.Client.ReadExchangeAsync(
            new Wire.ReadExchangeRequest { AskingAs = Inventory, ExchangeId = exchange }, cancellationToken: Ct);

        Assert.Equal(Wire.ReadExchangeReply.Types.Found.Found, read.Found);
        var message = Assert.Single(read.Messages);
        Assert.Equal(Orders, message.From);
        Assert.Equal(Billing, message.To);
        Assert.Contains("HISTORY, not mail", message.Envelope, StringComparison.Ordinal);
        Assert.Contains("gross of tax", message.Envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_number_that_names_nothing_is_said_not_to_exist()
    {
        var (broker, orders, billing, inventory, _) = await SetUpAsync();
        await using var _b = broker;
        await using var _o = orders;
        await using var _i = billing;
        await using var _n = inventory;

        var read = await inventory.Client.ReadExchangeAsync(
            new Wire.ReadExchangeRequest { AskingAs = Inventory, ExchangeId = 99_999 }, cancellationToken: Ct);

        Assert.Equal(Wire.ReadExchangeReply.Types.Found.NoSuchExchange, read.Found);
        Assert.Empty(read.Messages);
    }

    [Fact]
    public async Task A_search_returns_a_snippet_and_the_exchange_to_read()
    {
        var (broker, orders, billing, inventory, exchange) = await SetUpAsync();
        await using var _b = broker;
        await using var _o = orders;
        await using var _i = billing;
        await using var _n = inventory;

        var found = await inventory.Client.SearchHistoryAsync(
            new Wire.SearchHistoryRequest { AskingAs = Inventory, Words = "GROSS tax" }, cancellationToken: Ct);

        Assert.Equal(string.Empty, found.Refusal);
        var match = Assert.Single(found.Matches);
        Assert.Equal(exchange, match.ExchangeId);
        Assert.Contains("gross of tax", match.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_takes_nothing_from_what_the_receiver_is_owed()
    {
        var (broker, orders, billing, inventory, exchange) = await SetUpAsync();
        await using var _b = broker;
        await using var _o = orders;
        await using var _i = billing;
        await using var _n = inventory;

        await inventory.Client.ReadExchangeAsync(
            new Wire.ReadExchangeRequest { AskingAs = Inventory, ExchangeId = exchange }, cancellationToken: Ct);
        await inventory.Client.SearchHistoryAsync(
            new Wire.SearchHistoryRequest { AskingAs = Inventory, Words = "invoice" }, cancellationToken: Ct);

        // The reader has nothing waiting from it, and the receiver still collects the request.
        var readers = await inventory.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Inventory }, cancellationToken: Ct);
        var receiver = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);

        Assert.Empty(readers.Messages);
        Assert.Equal(exchange, Assert.Single(receiver.Messages).Message.ExchangeId);
    }

    [Fact]
    public async Task The_person_is_shown_who_read_and_what_was_searched_for()
    {
        var (broker, orders, billing, inventory, exchange) = await SetUpAsync();
        await using var _b = broker;
        await using var _o = orders;
        await using var _i = billing;
        await using var _n = inventory;

        await inventory.Client.ReadExchangeAsync(
            new Wire.ReadExchangeRequest { AskingAs = Inventory, ExchangeId = exchange }, cancellationToken: Ct);
        await inventory.Client.SearchHistoryAsync(
            new Wire.SearchHistoryRequest { AskingAs = Inventory, Words = "invoice total" }, cancellationToken: Ct);

        var conversation = (await broker.ConnectAsOperator().LocateExchangeAsync(
            new Wire.LocateExchangeRequest { ExchangeId = exchange }, cancellationToken: Ct)).ConversationId;
        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = conversation }, cancellationToken: Ct);

        var ofExchange = Assert.Single(export.Reads, r => r.ExchangeId == exchange);
        Assert.Equal(Inventory, ofExchange.Reader);

        var search = Assert.Single(export.Reads, r => r.ExchangeId == 0);
        Assert.Equal("invoice total", search.Words);
        Assert.Equal(1, search.Matches);
    }

    [Fact]
    public async Task A_refused_search_says_why_and_leaves_no_record()
    {
        var (broker, orders, billing, inventory, exchange) = await SetUpAsync();
        await using var _b = broker;
        await using var _o = orders;
        await using var _i = billing;
        await using var _n = inventory;

        var refused = await inventory.Client.SearchHistoryAsync(
            new Wire.SearchHistoryRequest { AskingAs = Inventory, Words = "   " }, cancellationToken: Ct);

        Assert.NotEqual(string.Empty, refused.Refusal);

        var conversation = (await broker.ConnectAsOperator().LocateExchangeAsync(
            new Wire.LocateExchangeRequest { ExchangeId = exchange }, cancellationToken: Ct)).ConversationId;
        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = conversation }, cancellationToken: Ct);

        Assert.Empty(export.Reads);
    }
}
