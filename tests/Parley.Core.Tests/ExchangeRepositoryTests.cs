using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Core.Tests;

public sealed class ExchangeRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;

    public ExchangeRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        SchemaMigrator.Migrate(_connection);

        var agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(new AgentRecord(
                name, "test", AgentTier.Full, null, null, null, null, AgentStatus.Registered))
                .GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Opening_persists_and_round_trips()
    {
        var outcome = await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);
        Assert.True(outcome.Succeeded);

        var loaded = await _exchanges.GetAsync(outcome.Exchange!.Id);

        Assert.NotNull(loaded);
        Assert.Equal(Orders, loaded.Asker);
        Assert.Equal(Billing, loaded.Answerer);
        Assert.Equal(Billing, loaded.OwedBy);
        Assert.Equal(ExchangeState.Open, loaded.State);
        Assert.Equal(T0, loaded.OpenedUtc);
        Assert.Equal(T0 + Ttl, loaded.ExpiresUtc);
        Assert.Equal(Ttl, loaded.Ttl);
    }

    [Fact]
    public async Task Reverse_question_on_a_busy_pair_returns_the_debt()
    {
        await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);

        // Billing owes Orders an answer, so Billing may not open its own question.
        var outcome = await _exchanges.TryOpenAsync(Billing, Orders, T0, Ttl);

        Assert.Equal(OpenRejection.PairBusy, outcome.Rejection);
        Assert.NotNull(outcome.ReciprocalDebt);
        Assert.Equal(Orders, outcome.ReciprocalDebt.Asker);
        Assert.Null(outcome.Blocking);
    }

    [Fact]
    public async Task Second_question_while_already_waiting_reports_the_blocker()
    {
        await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);

        var outcome = await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);

        Assert.Equal(OpenRejection.PairBusy, outcome.Rejection);
        Assert.Null(outcome.ReciprocalDebt);
        Assert.NotNull(outcome.Blocking);
    }

    [Fact]
    public async Task Three_cycle_is_rejected_with_a_path()
    {
        await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);
        await _exchanges.TryOpenAsync(Billing, Inventory, T0, Ttl);

        var outcome = await _exchanges.TryOpenAsync(Inventory, Orders, T0, Ttl);

        Assert.Equal(OpenRejection.WouldDeadlock, outcome.Rejection);
        Assert.Equal([Inventory, Orders, Billing], outcome.CyclePath);
    }

    [Fact]
    public async Task Closing_frees_the_pair_and_the_cycle()
    {
        await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);
        var second = await _exchanges.TryOpenAsync(Billing, Inventory, T0, Ttl);

        // Resolve the first leg.
        var first = (await _exchanges.GetOpenAsync()).Single(e => e.Asker == Orders);
        Assert.True(first.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        await _exchanges.SaveAsync(first);

        // The cycle no longer exists, so the third question is admitted.
        var outcome = await _exchanges.TryOpenAsync(Inventory, Orders, T0, Ttl);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(second.Exchange);
    }

    [Fact]
    public async Task Clarify_is_persisted_and_reverses_admission()
    {
        var opened = await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);
        var exchange = opened.Exchange!;

        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(2)).Succeeded);
        await _exchanges.SaveAsync(exchange);

        var loaded = await _exchanges.GetAsync(exchange.Id);

        Assert.NotNull(loaded);
        Assert.Equal(Orders, loaded.OwedBy);
        Assert.Equal(1, loaded.ClarifyDepth);
        Assert.Equal(T0.AddMinutes(2) + Ttl, loaded.ExpiresUtc);
    }

    [Fact]
    public async Task Orphaned_exchanges_are_findable_by_the_owing_agent()
    {
        var opened = await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);
        var exchange = opened.Exchange!;

        exchange.Orphan(T0.AddMinutes(20));
        await _exchanges.SaveAsync(exchange);

        var forBilling = await _exchanges.GetOrphanedOwedByAsync(Billing);
        var forOrders = await _exchanges.GetOrphanedOwedByAsync(Orders);

        Assert.Single(forBilling);
        Assert.Empty(forOrders);

        // An orphan holds no pair slot, so a new question on that pair is admitted.
        Assert.True((await _exchanges.TryOpenAsync(Billing, Orders, T0.AddHours(1), Ttl)).Succeeded);
    }

    [Fact]
    public async Task Expiry_scan_finds_only_overdue_open_exchanges()
    {
        await _exchanges.TryOpenAsync(Orders, Billing, T0, Ttl);
        await _exchanges.TryOpenAsync(Billing, Inventory, T0.AddHours(1), Ttl);

        var expired = await _exchanges.GetExpiredAsync(T0.AddMinutes(30));

        Assert.Single(expired);
        Assert.Equal(Orders, expired[0].Asker);
    }
}
