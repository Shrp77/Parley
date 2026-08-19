using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

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
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Opening_persists_and_round_trips()
    {
        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded);

        var loaded = await _exchanges.GetAsync(
            outcome.Exchange!.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(Orders, loaded.Asker);
        Assert.Equal(Billing, loaded.Answerer);
        Assert.Equal(Billing, loaded.OwedBy);
        Assert.Equal(ExchangeState.Open, loaded.State);
        Assert.Equal(T0, loaded.OpenedUtc);
        Assert.Equal(T0 + Ttl, loaded.ExpiresUtc);
        Assert.Equal(Ttl, loaded.Ttl);
        Assert.Equal(0L, loaded.Version);
    }

    [Fact]
    public async Task Sub_second_ttl_survives_the_round_trip()
    {
        var odd = TimeSpan.FromMilliseconds(1500);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, odd, cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await _exchanges.GetAsync(
            outcome.Exchange!.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(odd, loaded.Ttl);
        // The stored deadline and the stored window must agree with each other.
        Assert.Equal(loaded.OpenedUtc + loaded.Ttl, loaded.ExpiresUtc);
    }

    [Fact]
    public async Task Non_positive_ttl_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, TimeSpan.Zero,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Negative_clarify_depth_cap_is_rejected_as_an_argument()
    {
        // Without the up-front check this reached the schema and surfaced as a
        // SqliteException, leaking the storage layer through an API whose documented
        // failures are argument exceptions. The caller cannot act differently on the two,
        // and only one of them names the parameter at fault.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, Ttl, clarifyDepthCap: -1,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reverse_question_on_a_busy_pair_returns_the_debt()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Billing, Orders, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.PairBusy, outcome.Rejection);
        Assert.NotNull(outcome.ReciprocalDebt);
        Assert.Equal(Orders, outcome.ReciprocalDebt.Asker);
        Assert.Null(outcome.Blocking);
    }

    [Fact]
    public async Task Second_question_while_already_waiting_reports_the_blocker()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.PairBusy, outcome.Rejection);
        Assert.Null(outcome.ReciprocalDebt);
        Assert.NotNull(outcome.Blocking);
    }

    [Fact]
    public async Task A_question_to_an_unreachable_agent_is_refused_immediately()
    {
        // The failure this exists for: a repository is deleted while its agent is still
        // registered. The agent row survives, so the foreign key is satisfied and the pair slot
        // is free — every other admission check passes. Without this the question is accepted,
        // waits a full inactivity window, orphans unanswered, and costs the same again on the
        // next attempt, forever, while nobody learns why.
        var agents = new AgentRepository(_connection);
        Assert.Equal(1, await agents.MarkUnreachableAsync(
            Billing, TestContext.Current.CancellationToken));

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.AnswererUnreachable, outcome.Rejection);
        Assert.False(outcome.Succeeded);

        // The one refusal that hands back no work, because there is none to hand back.
        Assert.Null(outcome.Exchange);
        Assert.Null(outcome.ReciprocalDebt);
        Assert.Null(outcome.Blocking);
        Assert.Empty(outcome.CyclePath);

        // And nothing was written, so no slot is consumed by a question that cannot land.
        Assert.Empty(await _exchanges.GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_offline_agent_is_still_routable()
    {
        // Offline is the ordinary resting state of a lazily spawned agent, not a fault.
        // Refusing here would refuse the very traffic meant to wake it.
        var agents = new AgentRepository(_connection);
        Assert.Equal(3, await agents.MarkAllOfflineAsync(TestContext.Current.CancellationToken));

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task An_agent_that_comes_back_is_routable_again()
    {
        var agents = new AgentRepository(_connection);
        await agents.MarkUnreachableAsync(Billing, TestContext.Current.CancellationToken);
        await agents.MarkLiveAsync(Billing, null, T0, TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task An_unreachable_asker_may_still_ask()
    {
        // The state describes whether the broker can reach an agent, not whether it may speak.
        // An agent running well enough to ask is evidently reachable, and refusing it here
        // would strand it on a stale mark until something else cleared it.
        var agents = new AgentRepository(_connection);
        await agents.MarkUnreachableAsync(Orders, TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task Three_cycle_is_rejected_with_a_path()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        await _exchanges.TryOpenAsync(
            Billing, Inventory, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Inventory, Orders, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.WouldDeadlock, outcome.Rejection);
        Assert.Equal([Inventory, Orders, Billing], outcome.CyclePath);
    }

    [Fact]
    public async Task Closing_frees_the_pair_and_the_cycle()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        await _exchanges.TryOpenAsync(
            Billing, Inventory, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        var open = await _exchanges.GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var first = open.Single(e => e.Asker == Orders);

        Assert.True(first.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            first, cancellationToken: TestContext.Current.CancellationToken));

        var outcome = await _exchanges.TryOpenAsync(
            Inventory, Orders, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task Clarify_is_persisted_and_reverses_admission()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        var exchange = opened.Exchange!;

        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(2)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var loaded = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(Orders, loaded.OwedBy);
        Assert.Equal(1, loaded.ClarifyDepth);
        Assert.Equal(T0.AddMinutes(2) + Ttl, loaded.ExpiresUtc);
    }

    [Fact]
    public async Task Save_advances_the_version_stamp()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        var exchange = opened.Exchange!;
        Assert.Equal(0L, exchange.Version);

        exchange.Clarify(Billing, T0);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1L, exchange.Version);

        // The instance must remain usable for a second save without reloading.
        exchange.Clarify(Orders, T0);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2L, exchange.Version);
    }

    [Fact]
    public async Task Stale_save_is_rejected_and_loses_no_clarify_depth()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        var id = opened.Exchange!.Id;

        // Two independent instances of the same row, as two broker code paths would hold.
        var a = await _exchanges.GetAsync(id, TestContext.Current.CancellationToken);
        var b = await _exchanges.GetAsync(id, TestContext.Current.CancellationToken);

        a!.Clarify(Billing, T0);
        Assert.True(await _exchanges.SaveAsync(
            a, cancellationToken: TestContext.Current.CancellationToken));

        // b was loaded before a's write, so its transition must not be applied.
        b!.Clarify(Billing, T0);
        Assert.False(await _exchanges.SaveAsync(
            b, cancellationToken: TestContext.Current.CancellationToken));

        var reloaded = await _exchanges.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(1, reloaded!.ClarifyDepth);
        Assert.Equal(1L, reloaded.Version);
    }

    [Fact]
    public async Task Save_of_a_missing_row_reports_failure()
    {
        var orphanInstance = Exchange.Open(999_999, Orders, Billing, T0, Ttl);

        Assert.False(await _exchanges.SaveAsync(
            orphanInstance, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Orphaned_exchanges_are_findable_by_the_owing_agent()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        var exchange = opened.Exchange!;

        exchange.Orphan(T0.AddMinutes(20));
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(await _exchanges.GetOrphanedOwedByAsync(
            Billing, TestContext.Current.CancellationToken));
        Assert.Empty(await _exchanges.GetOrphanedOwedByAsync(
            Orders, TestContext.Current.CancellationToken));

        // An orphan holds no pair slot, so a new question on that pair is admitted.
        var reopened = await _exchanges.TryOpenAsync(
            Billing, Orders, T0.AddHours(1), Ttl,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(reopened.Succeeded);
    }

    [Fact]
    public async Task Resuming_a_second_orphan_for_the_same_pair_is_refused()
    {
        // ux_open_pair is a partial index over open rows only, so a pair can accumulate more
        // than one orphan: orphan the first, open a second on the freed slot, orphan that
        // too. Both are then returned by GetOrphanedOwedByAsync, and replaying the inbox on
        // re-registration would try to resume both.
        var first = (await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl,
            cancellationToken: TestContext.Current.CancellationToken)).Exchange!;
        first.Orphan(T0.AddMinutes(20));
        Assert.True(await _exchanges.SaveAsync(
            first, cancellationToken: TestContext.Current.CancellationToken));

        var second = (await _exchanges.TryOpenAsync(
            Orders, Billing, T0.AddHours(1), Ttl,
            cancellationToken: TestContext.Current.CancellationToken)).Exchange!;
        second.Orphan(T0.AddHours(2));
        Assert.True(await _exchanges.SaveAsync(
            second, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, (await _exchanges.GetOrphanedOwedByAsync(
            Billing, TestContext.Current.CancellationToken)).Count);

        // The first resume takes the pair slot back.
        first.Resume(T0.AddHours(3));
        Assert.True(await _exchanges.SaveAsync(
            first, cancellationToken: TestContext.Current.CancellationToken));

        // The second cannot, and the schema is what stops it. This surfaces as an exception
        // rather than a false return: false means "you are stale, reload and decide again",
        // which would send a caller that retried into a loop, since reloading changes
        // nothing about the pair already being held.
        second.Resume(T0.AddHours(3));
        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => _exchanges.SaveAsync(
                second, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expiry_scan_finds_only_overdue_open_exchanges()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Ttl, cancellationToken: TestContext.Current.CancellationToken);
        await _exchanges.TryOpenAsync(
            Billing, Inventory, T0.AddHours(1), Ttl,
            cancellationToken: TestContext.Current.CancellationToken);

        var expired = await _exchanges.GetExpiredAsync(
            T0.AddMinutes(30), TestContext.Current.CancellationToken);

        Assert.Single(expired);
        Assert.Equal(Orders, expired[0].Asker);
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _exchanges.GetOpenAsync(cancellationToken: cts.Token));
    }
}
