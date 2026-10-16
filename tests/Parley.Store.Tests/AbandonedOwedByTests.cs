using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Which debts an agent coming back is given again (§6).
/// </summary>
/// <remarks>
/// <para>
/// The query behind the attach sequence, tested where it lives rather than only through the broker. It is
/// one predicate and it decides whether an agent that returns picks up its work: a debt collected by a
/// session that ended is handed over again, and one never collected is simply delivered.
/// </para>
/// <para>
/// Nothing is orphaned, so a wrong answer costs a duplicate delivery or a missed one rather than lost work — but
/// the cases that make the predicate subtle are the same cases, and they are kept.
/// </para>
/// </remarks>
public sealed class AbandonedOwedByTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;

    public AbandonedOwedByTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing);
        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(Exchange Exchange, long RequestId)> OpenAsync(string text)
    {
        var (exchange, request) = await Work.OpenAsync(_connection, Orders, Billing, T0, Work.Request(text), cancellationToken: Token);
        return (exchange, request.Id);
    }

    private async Task<long> ClarifyAsync(Exchange exchange, AgentName from, AgentName to, string text, int minute)
    {
        Assert.True(exchange.Clarify(from, T0.AddMinutes(minute)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(exchange, cancellationToken: Token));

        var sent = await _messages.AppendAsync(
            exchange.Id, MessageType.Clarify, from, to, Work.Model, $$"""{"text":"{{text}}"}""", T0.AddMinutes(minute),
            cancellationToken: Token);

        return sent.Message.Id;
    }

    private async Task DeliverAsync(long messageId) =>
        Assert.True(await _messages.MarkDeliveredAsync(messageId, T0, Token));

    private async Task<IReadOnlyList<long>> AbandonedByAsync(AgentName agent) =>
        [.. (await _exchanges.GetAbandonedOwedByAsync(agent, Token)).Select(e => e.Id)];

    [Fact]
    public async Task A_request_the_agent_collected_and_sent_no_result_for_is_given_again()
    {
        // The case this is for: billing collected it, the session ended, nobody carried on.
        var (exchange, request) = await OpenAsync("where does price live");
        await DeliverAsync(request);

        Assert.Equal([exchange.Id], await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task A_request_still_waiting_in_the_inbox_is_not()
    {
        // Nobody has been handed this, so nobody left it: the agent attaching is the worker, arriving, and the
        // ordinary collection delivers it.
        await OpenAsync("where does price live");

        Assert.Empty(await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task A_reply_that_arrived_after_the_agent_left_is_not()
    {
        // Billing collected the Request, asked for detail, and closed before the detail arrived. The reply is
        // still undelivered, so the ordinary collection hands it over.
        var (exchange, request) = await OpenAsync("where does price live");
        await DeliverAsync(request);
        await DeliverAsync(await ClarifyAsync(exchange, Billing, Orders, "which?", 1));
        await ClarifyAsync(exchange, Orders, Billing, "of VAT", 2);

        Assert.Empty(await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task A_debt_the_agent_is_up_to_date_on_is_given_again_even_mid_exchange()
    {
        // The other side of the same case, so the rule is not simply "never anything clarified".
        var (exchange, request) = await OpenAsync("where does price live");
        await DeliverAsync(request);
        await DeliverAsync(await ClarifyAsync(exchange, Billing, Orders, "which?", 1));
        await DeliverAsync(await ClarifyAsync(exchange, Orders, Billing, "of VAT", 2));

        Assert.Equal([exchange.Id], await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task An_exchange_with_no_message_at_all_is_not()
    {
        // Unreachable through the broker, which writes the Request and the exchange in one transaction. Kept
        // because a redelivery of nothing is a query that has to cope with it.
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, MessageIdentity.ComputeRequest(Orders, Billing, Work.Request()), Work.Conversation,
            cancellationToken: Token);

        Assert.True(opened.Succeeded);
        Assert.Empty(await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task The_agent_waiting_on_the_result_has_nothing_given_again()
    {
        // owed_by, not "party to". Orders is waiting; somebody else holds the work.
        var (_, request) = await OpenAsync("where does price live");
        await DeliverAsync(request);

        Assert.Empty(await AbandonedByAsync(Orders));
    }

    [Fact]
    public async Task A_closed_exchange_is_never_given_again()
    {
        var (exchange, request) = await OpenAsync("where does price live");
        await DeliverAsync(request);
        await Work.CloseAsync(_connection, exchange, T0.AddMinutes(1), cancellationToken: Token);

        Assert.Empty(await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task One_uncollected_request_does_not_shield_another_exchange()
    {
        // Per exchange, not per agent. Otherwise a single unopened message would hold every other debt back.
        await OpenAsync("where does price live");

        var (ignored, request) = await OpenAsync("who owns the tax rate");
        await DeliverAsync(request);

        Assert.Equal([ignored.Id], await AbandonedByAsync(Billing));
    }
}
