using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

public sealed class ReaperTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    // Far enough in the past that it never governs the ghosting clock; the outage case
    // has its own tests.
    private static readonly DateTimeOffset BrokerStart = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delivery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Working = TimeSpan.FromHours(4);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(3);

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly Reaper _reaper;

    public ReaperTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null, AgentStatus.Registered),
                TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
        _reaper = new Reaper(_exchanges);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Exchange> Open(
        AgentName asker, AgentName answerer, DateTimeOffset at, string question = "q1")
    {
        var outcome = await _exchanges.TryOpenAsync(
            asker, answerer, at, question,
            cancellationToken: TestContext.Current.CancellationToken);

        // Asserted rather than assumed. A refusal here returns a null exchange that only
        // surfaces later as a confusing assertion failure somewhere else in the test — which
        // is exactly how the cycle rejection in one of these scenarios first showed up.
        Assert.True(outcome.Succeeded, $"expected to open {asker} -> {answerer}, got {outcome.Rejection}");
        return outcome.Exchange!;
    }

    [Fact]
    public async Task A_sweep_over_nothing_expired_does_nothing()
    {
        await Open(Orders, Billing, T0);

        var result = await _reaper.SweepAsync(
            T0.AddMinutes(1), BrokerStart, TestContext.Current.CancellationToken);

        Assert.Equal(ReapResult.Nothing, result);
        Assert.Equal(0, result.Orphaned);
    }

    [Fact]
    public async Task An_expired_exchange_is_orphaned()
    {
        var exchange = await Open(Orders, Billing, T0);

        var result = await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Orphaned);
        Assert.Equal(0, result.Contended);

        var loaded = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Orphaned, loaded!.State);
    }

    [Fact]
    public async Task Orphaning_frees_the_question_to_be_asked_again()
    {
        // Under pair serialisation this test showed a released slot. Nothing serialises now, so
        // what orphaning frees is narrower and more precise: the distinctness rule refuses a
        // repeat only while the original is open, so an abandoned question can be re-asked once
        // the sweep has closed the book on it.
        await Open(Orders, Billing, T0);

        var repeat = await _exchanges.TryOpenAsync(
            Orders, Billing, T0.AddMinutes(1), "q1",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(OpenRejection.DuplicateQuestion, repeat.Rejection);

        await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);

        var admitted = await _exchanges.TryOpenAsync(
            Orders, Billing, T0 + Delivery, "q1",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(admitted.Succeeded, $"expected admission, got {admitted.Rejection}");
    }

    [Fact]
    public async Task Orphaning_releases_the_wait_for_edge()
    {
        // Releasing the slot is not enough on its own: a stuck exchange also holds an edge in
        // the wait-for graph, and any cycle running through it stays closed. Here orders is
        // waiting on billing and billing on inventory, so inventory cannot ask orders until
        // one of those clears.
        await Open(Orders, Billing, T0);
        await Open(Billing, Inventory, T0.AddHours(1));

        var refused = await _exchanges.TryOpenAsync(
            Inventory, Orders, T0.AddHours(1), "q1",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(OpenRejection.WouldDeadlock, refused.Rejection);

        // Only the first has expired at this instant.
        var result = await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Orphaned);

        var admitted = await _exchanges.TryOpenAsync(
            Inventory, Orders, T0 + Delivery, "q1",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(admitted.Succeeded);
    }

    [Fact]
    public async Task A_sweep_orphans_every_expired_exchange_it_finds()
    {
        await Open(Orders, Billing, T0);
        await Open(Billing, Inventory, T0);

        // Deliberately orders -> inventory, not inventory -> orders: the latter would close the
        // cycle the first two describe and be refused, leaving this test asserting nothing.
        await Open(Orders, Inventory, T0.AddHours(5));   // still live at the sweep instant

        var result = await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Orphaned);

        var open = await _exchanges.GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(open);
    }

    [Fact]
    public async Task A_closed_exchange_is_not_reaped()
    {
        // Closed exchanges hold no slot and block nobody, so their deadline is meaningless.
        var exchange = await Open(Orders, Billing, T0);
        Assert.True(exchange.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var result = await _reaper.SweepAsync(
            T0.AddHours(9), BrokerStart, TestContext.Current.CancellationToken);

        Assert.Equal(ReapResult.Nothing, result);
    }

    [Fact]
    public async Task An_already_orphaned_exchange_is_not_reaped_twice()
    {
        await Open(Orders, Billing, T0);

        var first = await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);
        var second = await _reaper.SweepAsync(T0 + Delivery + Delivery, BrokerStart, TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Orphaned);
        Assert.Equal(ReapResult.Nothing, second);
    }

    [Fact]
    public async Task An_exchange_answered_during_the_sweep_is_left_alone()
    {
        // The race the version guard exists for. The scan saw it open and expired; by the time
        // the write lands the answerer has closed it. Retrying would be wrong — the thing that
        // expired no longer exists in that form — and overwriting would discard a real answer.
        var exchange = await Open(Orders, Billing, T0);

        var stale = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.True(stale!.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            stale, cancellationToken: TestContext.Current.CancellationToken));

        // The sweep reads the row fresh, so it now sees a closed exchange and finds nothing.
        var result = await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);

        Assert.Equal(ReapResult.Nothing, result);

        var loaded = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Closed, loaded!.State);
    }

    [Fact]
    public async Task The_deadline_is_inclusive()
    {
        // Expiry is 'at or past the deadline', matching the scan's expires_utc <= now and
        // IsExpiredAt's now >= ExpiresUtc. Pinned because the two are written as mirror images
        // and an off-by-one in either would only show up as exchanges reaped a tick early or
        // late.
        await Open(Orders, Billing, T0);

        var justBefore = await _reaper.SweepAsync(
            T0 + Delivery - TimeSpan.FromTicks(1), BrokerStart, TestContext.Current.CancellationToken);
        Assert.Equal(ReapResult.Nothing, justBefore);

        var exactly = await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);
        Assert.Equal(1, exactly.Orphaned);
    }

    [Fact]
    public async Task A_clarification_postpones_the_deadline()
    {
        // The TTL is an inactivity window, not a total budget: each message earns a fresh one.
        // A long exchange that is actively progressing must not be reaped out from under its
        // participants.
        var exchange = await Open(Orders, Billing, T0);
        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(10)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var early = await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);
        Assert.Equal(ReapResult.Nothing, early);

        var later = await _reaper.SweepAsync(
            T0.AddMinutes(10) + Working, BrokerStart, TestContext.Current.CancellationToken);
        Assert.Equal(1, later.Orphaned);
    }

    [Fact]
    public async Task An_orphaned_exchange_keeps_its_clarify_depth()
    {
        // Reaping must not become a way to reset the depth cap. An exchange that cycled through
        // orphan and resume would otherwise get a fresh budget for going in circles.
        var exchange = await Open(Orders, Billing, T0);
        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        await _reaper.SweepAsync(
            T0.AddMinutes(1) + Working, BrokerStart, TestContext.Current.CancellationToken);

        var loaded = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Orphaned, loaded!.State);
        Assert.Equal(1, loaded.ClarifyDepth);
    }

    [Fact]
    public async Task The_owing_agent_can_still_find_its_orphan()
    {
        // Orphaning preserves the question rather than discarding it. Whether the broker then
        // replays it is the operator's resume policy, per SPEC 14, and not the reaper's call.
        await Open(Orders, Billing, T0);

        await _reaper.SweepAsync(T0 + Delivery, BrokerStart, TestContext.Current.CancellationToken);

        Assert.Single(await _exchanges.GetOrphanedOwedByAsync(
            Billing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        await Open(Orders, Billing, T0);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _reaper.SweepAsync(T0 + Delivery, BrokerStart, cancelled.Token));
    }

    [Fact]
    public void A_reaper_needs_a_repository()
    {
        Assert.Throws<ArgumentNullException>(() => new Reaper(null!));
    }
}
