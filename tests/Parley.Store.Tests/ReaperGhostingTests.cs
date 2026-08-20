using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The half of the sweep that closes what nobody came back for.
/// </summary>
/// <remarks>
/// Orphaning and ghosting are one walk over the same deadlines, and separating them would
/// leave two schedules to keep in step. They protect different parties, though, which is why
/// their windows differ by orders of magnitude: orphaning frees peers from an edge a vanished
/// agent was holding, and is measured in minutes because that blockage spreads; ghosting tells
/// an asker to stop expecting an answer, and is measured in hours because being wrong means
/// telling an agent to proceed alone while its peer was minutes from replying.
/// </remarks>
public sealed class ReaperGhostingTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delivery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Working = TimeSpan.FromHours(4);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(3);

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly Reaper _reaper;

    public ReaperGhostingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
        _reaper = new Reaper(_exchanges);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Exchange> Open(string question = "q1")
    {
        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, question,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, $"expected to open, got {outcome.Rejection}");
        return outcome.Exchange!;
    }

    [Fact]
    public async Task An_orphan_nobody_returns_for_is_ghosted()
    {
        var exchange = await Open();

        var orphaning = await _reaper.SweepAsync(
            T0 + Delivery, LongAgo, TestContext.Current.CancellationToken);
        Assert.Equal(1, orphaning.Orphaned);
        Assert.Equal(0, orphaning.Ghosted);

        var ghosting = await _reaper.SweepAsync(
            T0 + Delivery + Grace, LongAgo, TestContext.Current.CancellationToken);
        Assert.Equal(1, ghosting.Ghosted);

        var loaded = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Closed, loaded!.State);
        Assert.Equal(Resolution.Ghosted, loaded.Resolution);
    }

    [Fact]
    public async Task A_sweep_does_not_ghost_what_it_has_just_orphaned()
    {
        // The grace period has to actually elapse. Ghosting in the same sweep that orphaned an
        // exchange would mean a grace period of zero, which the schema forbids for the reason
        // this test states: the resume policy would never get a chance to run.
        await Open();

        var result = await _reaper.SweepAsync(
            T0 + Delivery, LongAgo, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Orphaned);
        Assert.Equal(0, result.Ghosted);
    }

    [Fact]
    public async Task An_orphan_still_inside_its_grace_period_is_left_alone()
    {
        await Open();
        await _reaper.SweepAsync(T0 + Delivery, LongAgo, TestContext.Current.CancellationToken);

        var early = await _reaper.SweepAsync(
            T0 + Delivery + Grace - TimeSpan.FromMinutes(1), LongAgo,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, early.Ghosted);
    }

    [Fact]
    public async Task An_orphan_that_was_resumed_is_not_ghosted()
    {
        // Resumption is the outcome the grace period exists to wait for.
        var exchange = await Open();
        await _reaper.SweepAsync(T0 + Delivery, LongAgo, TestContext.Current.CancellationToken);

        var orphan = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.True(orphan!.Resume(T0.AddHours(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            orphan, cancellationToken: TestContext.Current.CancellationToken));

        var later = await _reaper.SweepAsync(
            T0.AddDays(1), LongAgo, TestContext.Current.CancellationToken);

        Assert.Equal(0, later.Ghosted);
    }

    [Fact]
    public async Task A_broker_that_was_down_does_not_ghost_everything_on_start_up()
    {
        // The failure the clock rule exists for, end to end. Orphaned before an outage, the
        // grace period elapses while nothing can possibly resume it, and a wall-clock rule
        // would close it within a second of the broker returning.
        await Open();
        await _reaper.SweepAsync(T0 + Delivery, LongAgo, TestContext.Current.CancellationToken);

        var brokerStart = T0.AddDays(1);

        var immediately = await _reaper.SweepAsync(
            brokerStart, brokerStart, TestContext.Current.CancellationToken);
        Assert.Equal(0, immediately.Ghosted);

        var afterGrace = await _reaper.SweepAsync(
            brokerStart + Grace, brokerStart, TestContext.Current.CancellationToken);
        Assert.Equal(1, afterGrace.Ghosted);
    }

    [Fact]
    public async Task A_ghosted_exchange_frees_its_question_to_be_asked_again()
    {
        // The asker has been told nobody answered. If the question still matters after that, it
        // deserves a fresh attempt — possibly of a peer that exists.
        await Open("where does price live");
        await _reaper.SweepAsync(T0 + Delivery, LongAgo, TestContext.Current.CancellationToken);
        await _reaper.SweepAsync(
            T0 + Delivery + Grace, LongAgo, TestContext.Current.CancellationToken);

        var again = await _exchanges.TryOpenAsync(
            Orders, Billing, T0.AddDays(1), "where does price live",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(again.Succeeded, $"expected admission, got {again.Rejection}");
    }

    [Fact]
    public async Task Ghosting_does_not_touch_an_exchange_that_was_answered()
    {
        var exchange = await Open();
        Assert.True(exchange.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var result = await _reaper.SweepAsync(
            T0.AddDays(30), LongAgo, TestContext.Current.CancellationToken);

        Assert.Equal(ReapResult.Nothing, result);

        var loaded = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Resolution.Answered, loaded!.Resolution);
    }

    [Fact]
    public async Task One_sweep_may_both_orphan_and_ghost()
    {
        // Different exchanges at different stages, which is the ordinary steady state once a
        // deployment has been running for a while.
        var first = await Open("first question");
        await _reaper.SweepAsync(T0 + Delivery, LongAgo, TestContext.Current.CancellationToken);

        // Opened later, so its delivery window expires later.
        var second = await _exchanges.TryOpenAsync(
            Orders, Billing, T0 + Grace, "second question",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(second.Succeeded);

        var result = await _reaper.SweepAsync(
            T0 + Delivery + Grace, LongAgo, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Orphaned);
        Assert.Equal(1, result.Ghosted);

        var loadedFirst = await _exchanges.GetAsync(first.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Resolution.Ghosted, loadedFirst!.Resolution);
    }

    [Fact]
    public async Task A_sweep_over_nothing_reports_nothing()
    {
        Assert.Equal(
            ReapResult.Nothing,
            await _reaper.SweepAsync(T0, LongAgo, TestContext.Current.CancellationToken));
    }
}
