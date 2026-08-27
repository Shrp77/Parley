using System.Text.Json;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Which debts SPEC.md §14 counts as abandoned.
/// </summary>
/// <remarks>
/// <para>
/// The query behind the attach sequence, tested where it lives rather than only through the
/// broker. It is one predicate and it decides whether a deployment loses questions: orphaning
/// on <c>owed_by</c> alone destroys every question asked to a closed session, in the moment
/// before its recipient opens one and could read it. With one agent per repository, and most
/// repositories closed most of the time, that is most questions.
/// </para>
/// <para>
/// That defect was live and green for a full review rotation. It was not visible by reading the
/// sequence, which is correct on its own terms — a debt whose worker vanished should be
/// released. What it took was writing down the case where the worker had not vanished but had
/// not yet arrived.
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

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static string Payload(string text) => JsonSerializer.Serialize(new
    {
        text,
        consequences = "If the answer is yes I read it from you; if not I define my own."
    });

    private async Task<Exchange> OpenAsync(string question)
    {
        var ct = TestContext.Current.CancellationToken;

        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, question, cancellationToken: ct);

        Assert.True(opened.Succeeded, $"expected to open, got {opened.Rejection}");

        return opened.Exchange!;
    }

    private async Task<long> SendAsync(
        long exchangeId, MessageType type, AgentName from, AgentName to, string text)
    {
        var ct = TestContext.Current.CancellationToken;

        var sent = await _messages.AppendAsync(
            exchangeId, type, from, to, "test-model", Payload(text), T0, cancellationToken: ct);

        Assert.True(sent.Stored, "expected the message to be stored");

        return sent.Message.Id;
    }

    private async Task DeliverAsync(long messageId) =>
        Assert.True(await _messages.MarkDeliveredAsync(
            messageId, T0, TestContext.Current.CancellationToken));

    private async Task<IReadOnlyList<long>> AbandonedByAsync(AgentName agent) =>
        [.. (await _exchanges.GetAbandonedOwedByAsync(
            agent, TestContext.Current.CancellationToken)).Select(e => e.Id)];

    [Fact]
    public async Task A_question_the_agent_has_collected_and_not_answered_is_abandoned()
    {
        // The case §14 is for: billing read it, the session ended, nobody carried on.
        var exchange = await OpenAsync("where does price live");
        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Question, Orders, Billing, "?"));

        Assert.Equal([exchange.Id], await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task A_question_still_waiting_in_the_inbox_is_not()
    {
        // Asked into a closed session. Nobody has been handed this, so nobody abandoned it —
        // the agent attaching is the worker, arriving.
        var exchange = await OpenAsync("where does price live");
        await SendAsync(exchange.Id, MessageType.Question, Orders, Billing, "?");

        Assert.Empty(await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task A_reply_that_arrived_after_the_agent_left_is_not()
    {
        // Why the test is what is still pending rather than what has been seen. Billing read the
        // question, asked for detail, and closed before the detail arrived: it has seen traffic
        // on this exchange but has not seen the turn it now owes an answer to. Orphaning it here
        // discards a peer's reply that nobody has read — the same defect one step further in.
        var exchange = await OpenAsync("where does price live");
        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Question, Orders, Billing, "?"));

        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Clarify, Billing, Orders, "which?"));

        Assert.True(exchange.Clarify(Orders, T0.AddMinutes(2)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        // Orders' reply is sent and not collected. Billing owes the answer again.
        await SendAsync(exchange.Id, MessageType.Clarify, Orders, Billing, "of VAT");

        Assert.Empty(await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task A_debt_the_agent_is_up_to_date_on_is_abandoned_even_mid_exchange()
    {
        // The other side of the same case, so the rule is not simply "never orphan anything
        // that has been clarified". Once billing has collected orders' reply, the debt is one it
        // is carrying, and an attach releases it.
        var exchange = await OpenAsync("where does price live");
        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Question, Orders, Billing, "?"));

        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Clarify, Billing, Orders, "which?"));

        Assert.True(exchange.Clarify(Orders, T0.AddMinutes(2)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Clarify, Orders, Billing, "of VAT"));

        Assert.Equal([exchange.Id], await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task An_exchange_with_no_message_at_all_is_not()
    {
        // Unreachable through the broker, which writes the question and the exchange in one
        // transaction. Kept because the costs are not symmetric: orphaning something the agent
        // was never handed destroys a question, and declining to orphan it leaves the exchange
        // to expire on its own deadline a few minutes later.
        var exchange = await OpenAsync("where does price live");

        Assert.Empty(await AbandonedByAsync(Billing));
        Assert.Equal(ExchangeState.Open, exchange.State);
    }

    [Fact]
    public async Task The_agent_waiting_on_the_answer_has_nothing_abandoned()
    {
        // owed_by, not "party to". Orders is waiting; somebody else is working towards this
        // deadline, and orders restarting is no evidence about them.
        var exchange = await OpenAsync("where does price live");
        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Question, Orders, Billing, "?"));

        Assert.Empty(await AbandonedByAsync(Orders));
    }

    [Fact]
    public async Task A_closed_exchange_is_not_abandoned_however_it_was_delivered()
    {
        var exchange = await OpenAsync("where does price live");
        await DeliverAsync(await SendAsync(exchange.Id, MessageType.Question, Orders, Billing, "?"));

        Assert.True(exchange.Answer(Billing, T0.AddMinutes(1), Resolution.Answered).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(await AbandonedByAsync(Billing));
    }

    [Fact]
    public async Task One_agents_uncollected_question_does_not_shield_another_exchange()
    {
        // The predicate is per exchange, not per agent. An agent with one question outstanding
        // and one collected-and-ignored releases exactly the second — otherwise a single
        // unopened message would hold every other debt open indefinitely.
        var waiting = await OpenAsync("where does price live");
        await SendAsync(waiting.Id, MessageType.Question, Orders, Billing, "?");

        var ignored = await OpenAsync("who owns the tax rate");
        await DeliverAsync(await SendAsync(ignored.Id, MessageType.Question, Orders, Billing, "?"));

        Assert.Equal([ignored.Id], await AbandonedByAsync(Billing));
    }
}
