using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The reboot sequence and the maintenance tick.
/// </summary>
/// <remarks>
/// Every piece exercised here already existed and was already tested. What was missing was
/// anything that decided when to call it, so these are about sequencing rather than about the
/// transitions themselves — that an exchange interrupted by a restart does not keep a deadline
/// nobody is working towards, that a retirement outlives the restart while an unreachable mark
/// does not, and that a sweep refuses to run before it knows when the broker started.
/// </remarks>
public sealed class BrokerLifecycleTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;

    public BrokerLifecycleTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    private Broker NewBroker(BrokerOptions? options = null) => new(_connection, options);

    private async Task<Exchange> Open(AgentName asker, AgentName answerer, string question) =>
        (await _exchanges.TryOpenAsync(
            asker, answerer, T0, question,
            cancellationToken: TestContext.Current.CancellationToken)).Exchange
        ?? throw new InvalidOperationException($"expected to open {asker} -> {answerer}");

    private async Task<AgentStatus> StatusOf(AgentName name) =>
        (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == name).Status;

    // ---- the reboot sequence ----

    [Fact]
    public async Task Starting_orphans_every_open_exchange()
    {
        // An exchange left open across a restart keeps a deadline nobody is working towards,
        // and a wait-for edge blocking peers on behalf of an agent that is not running.
        var first = await Open(Orders, Billing, "q1");
        var second = await Open(Billing, Inventory, "q2");

        var result = await NewBroker().StartAsync(T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExchangesOrphaned);

        foreach (var id in new[] { first.Id, second.Id })
        {
            var loaded = await _exchanges.GetAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(ExchangeState.Orphaned, loaded!.State);
        }
    }

    [Fact]
    public async Task Starting_clears_stale_liveness_including_an_unreachable_mark()
    {
        // Reinstalling or re-cloning a repository is exactly the kind of thing that happens
        // between runs, so a mark the previous run made gets to be proved again.
        await _agents.MarkLiveAsync(Orders, "sess-1", T0, TestContext.Current.CancellationToken);
        await _agents.MarkUnreachableAsync(Billing, TestContext.Current.CancellationToken);

        var result = await NewBroker().StartAsync(T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.AgentsMarkedOffline);
        Assert.Equal(AgentStatus.Offline, await StatusOf(Orders));
        Assert.Equal(AgentStatus.Offline, await StatusOf(Billing));
    }

    [Fact]
    public async Task Starting_leaves_a_retirement_alone()
    {
        // The one judgement a restart is not evidence against.
        await _agents.RetireAsync(Inventory, TestContext.Current.CancellationToken);

        var result = await NewBroker().StartAsync(T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.AgentsMarkedOffline);
        Assert.Equal(AgentStatus.Retired, await StatusOf(Inventory));
    }

    [Fact]
    public async Task Starting_over_a_quiet_deployment_does_nothing_surprising()
    {
        var result = await NewBroker().StartAsync(T0, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExchangesOrphaned);
        Assert.Equal(3, result.AgentsMarkedOffline);
    }

    [Fact]
    public async Task Orphaning_at_start_up_starts_the_grace_period_from_then()
    {
        // Not from when the exchange was opened. An exchange interrupted by a restart has not
        // been abandoned by anybody yet, so its grace period begins when the broker returns.
        await Open(Orders, Billing, "q1");

        var brokerStart = T0.AddDays(3);
        var broker = NewBroker();
        await broker.StartAsync(brokerStart, TestContext.Current.CancellationToken);

        var beforeGrace = await broker.TickAsync(
            brokerStart + ExchangePolicy.Default.GracePeriod - TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, beforeGrace.Ghosted);

        var afterGrace = await broker.TickAsync(
            brokerStart + ExchangePolicy.Default.GracePeriod,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, afterGrace.Ghosted);
    }

    [Fact]
    public async Task Starting_again_moves_the_ghosting_clock_forward()
    {
        // Worth pinning because it is a footgun as much as a feature. A genuine restart should
        // give every orphan a fresh window, since the outage consumed the old one through no
        // fault of the agent. A host that calls StartAsync twice by accident gets the same
        // behaviour and silently delays ghosting, with nothing to notice.
        //
        // Left as it is rather than guarded: refusing a second start would break the case this
        // exists for, which is a process that restarts. The contract is that a host calls it
        // once per process lifetime, and it is documented on the method.
        await Open(Orders, Billing, "q1");

        var broker = NewBroker();
        await broker.StartAsync(T0, TestContext.Current.CancellationToken);

        var ghostingDue = T0 + ExchangePolicy.Default.GracePeriod;
        Assert.Equal(1, (await broker.TickAsync(
            ghostingDue, TestContext.Current.CancellationToken)).Ghosted);

        // A second exchange, a second start, and the clock restarts from there.
        await Open(Orders, Billing, "q2");
        var secondStart = T0.AddDays(1);
        await broker.StartAsync(secondStart, TestContext.Current.CancellationToken);

        Assert.Equal(secondStart, broker.StartedUtc);
        Assert.Equal(0, (await broker.TickAsync(
            secondStart + ExchangePolicy.Default.GracePeriod - TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken)).Ghosted);
        Assert.Equal(1, (await broker.TickAsync(
            secondStart + ExchangePolicy.Default.GracePeriod,
            TestContext.Current.CancellationToken)).Ghosted);
    }

    // ---- the tick ----

    [Fact]
    public async Task Ticking_before_starting_is_refused()
    {
        // The ghosting clock is measured from broker start. A sweep without one falls back to
        // wall-clock timing and ghosts every orphan that outlived an outage the moment the
        // process returns, which is the exact failure the clock rule exists to prevent.
        var broker = NewBroker();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.TickAsync(T0, TestContext.Current.CancellationToken));

        Assert.Contains("StartAsync", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticking_orphans_what_has_run_out_of_time()
    {
        var broker = NewBroker();
        await broker.StartAsync(T0, TestContext.Current.CancellationToken);

        var exchange = await Open(Orders, Billing, "q1");

        var result = await broker.TickAsync(
            T0 + ExchangePolicy.Default.DeliveryWindow, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Orphaned);

        var loaded = await _exchanges.GetAsync(exchange.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Orphaned, loaded!.State);
    }

    [Fact]
    public async Task The_broker_records_when_it_started()
    {
        var broker = NewBroker();
        Assert.Null(broker.StartedUtc);

        await broker.StartAsync(T0, TestContext.Current.CancellationToken);

        Assert.Equal(T0, broker.StartedUtc);
    }

    // ---- configuration ----

    [Fact]
    public async Task A_start_that_is_cancelled_part_way_leaves_the_broker_unstarted()
    {
        // Rather than one that ticks having half-orphaned its exchanges. A sweep reads
        // StartedUtc to time ghosting, and a start that did not finish is not a start.
        await Open(Orders, Billing, "q1");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var broker = NewBroker();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => broker.StartAsync(T0, cancelled.Token));

        Assert.Null(broker.StartedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.TickAsync(T0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_broker_needs_a_connection()
    {
        Assert.Throws<ArgumentNullException>(() => new Broker(null!));
    }

    [Fact]
    public void A_policy_storage_would_refuse_is_caught_at_construction()
    {
        // Rather than on the first question, with a constraint violation several layers below
        // the mistake.
        var options = BrokerOptions.Default with
        {
            ExchangePolicy = ExchangePolicy.Default with { GracePeriod = TimeSpan.Zero }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => NewBroker(options));
    }

    [Fact]
    public void An_undefined_resume_policy_is_caught_at_construction()
    {
        var options = BrokerOptions.Default with { ResumePolicy = (ResumePolicy)99 };

        Assert.Throws<ArgumentOutOfRangeException>(() => NewBroker(options));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_missing_member_of_the_configuration_is_refused(bool nullPolicy)
    {
        // Validate guards both and nothing checked that it did, which is how a guard gets
        // deleted in a refactor without anything noticing. A null gate would be a null reference
        // at the first replay, and a null policy at the first question.
        var options = nullPolicy
            ? BrokerOptions.Default with { ExchangePolicy = null! }
            : BrokerOptions.Default with { ResumeGate = null! };

        Assert.Throws<ArgumentNullException>(() => NewBroker(options));
    }

    [Fact]
    public async Task Resuming_observes_cancellation_part_way_through()
    {
        // Cancels from inside the gate, after the first exchange has been considered, so the
        // pass is stopped half way rather than before it starts.
        //
        // Worth being exact about what this does not prove. The loop opens with an explicit
        // ThrowIfCancellationRequested, and deleting it does not fail this test, because every
        // awaited call in the body already honours the token — the gate, and the save after it.
        // The check is therefore defensive rather than load-bearing today: it keeps the loop
        // cancellable independently of what the body happens to do, which a body that gained a
        // synchronous step would otherwise lose. This verifies the observable behaviour, which
        // is that a cancelled pass stops.
        await Open(Orders, Billing, "q1");
        await Open(Orders, Billing, "q2");

        using var cancelled = new CancellationTokenSource();
        var broker = NewBroker(BrokerOptions.Default with
        {
            ResumePolicy = ResumePolicy.Always,
            ResumeGate = new CancellingGate(cancelled)
        });
        await broker.StartAsync(T0, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => broker.ResumeOwedByAsync(Billing, T0.AddMinutes(1), cancelled.Token));
    }

    /// <summary>Permits the first replay, then cancels, so the loop must notice.</summary>
    private sealed class CancellingGate(CancellationTokenSource source) : IResumeGate
    {
        public async ValueTask<bool> MayResumeAsync(
            Exchange exchange, CancellationToken cancellationToken = default)
        {
            await source.CancelAsync();
            return true;
        }
    }

    [Fact]
    public void The_default_configuration_never_resumes()
    {
        // SPEC.md §14 makes never the safe default for interactive use: work resuming that
        // somebody stopped on purpose is the surprise worth avoiding, so an unattended
        // deployment opts into always rather than inheriting it.
        Assert.Equal(ResumePolicy.Never, BrokerOptions.Default.ResumePolicy);
    }
}
