using System.Text.Json;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The attach sequence and the maintenance tick.
/// </summary>
/// <remarks>
/// <para>
/// Every piece exercised here already existed and was already tested. What was missing was
/// anything that decided when to call it, so these are about sequencing rather than about the
/// transitions themselves — that a debt whose worker vanished does not keep a deadline nobody
/// is working towards, that a retirement outlives an attach, and that a sweep refuses to run
/// before it knows when the broker started.
/// </para>
/// <para>
/// The sequence is scoped to the attaching agent, per SPEC.md §14, and much of what follows is
/// about the half that scoping protects: a peer's live work. The broker-wide version these
/// replaced was correct while a broker was one service hosting every agent, and became a hazard
/// the moment agents were separately launched sessions — the second one to start orphaned the
/// first's work, silently.
/// </para>
/// </remarks>
public sealed class BrokerLifecycleTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;

    public BrokerLifecycleTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    private Broker NewBroker(BrokerOptions? options = null) => new(_connection, options);

    /// <summary>
    /// Opens an exchange and puts the question in front of the answerer, as an ask followed by
    /// a drain would.
    /// </summary>
    /// <remarks>
    /// The delivery is not decoration. §14 releases a debt the attaching agent was *carrying*,
    /// and carrying it means having been handed it — an exchange whose question is still pending
    /// in the answerer's inbox belongs to an agent that has not had its turn yet, and orphaning
    /// it on attach would destroy the question a moment before it could be read. So an exchange
    /// built for these tests has to reach the state the broker actually produces, which is a
    /// message row that has been collected. <see cref="OpenUndelivered"/> is the other half.
    /// </remarks>
    /// <param name="asker">The asking agent.</param>
    /// <param name="answerer">The agent that owes the answer.</param>
    /// <param name="question">The question payload.</param>
    /// <returns>The opened exchange.</returns>
    private async Task<Exchange> Open(AgentName asker, AgentName answerer, string question)
    {
        var exchange = await OpenUndelivered(asker, answerer, question);
        var ct = TestContext.Current.CancellationToken;

        foreach (var message in await _messages.GetPendingAsync(answerer, cancellationToken: ct))
        {
            await _messages.MarkDeliveredAsync(message.Id, T0, ct);
        }

        return exchange;
    }

    /// <summary>Opens an exchange and leaves the question uncollected.</summary>
    /// <param name="asker">The asking agent.</param>
    /// <param name="answerer">The agent that owes the answer.</param>
    /// <param name="question">The question payload.</param>
    /// <returns>The opened exchange.</returns>
    private async Task<Exchange> OpenUndelivered(
        AgentName asker, AgentName answerer, string question)
    {
        var ct = TestContext.Current.CancellationToken;

        var exchange = (await _exchanges.TryOpenAsync(
            asker, answerer, T0, question, cancellationToken: ct)).Exchange
            ?? throw new InvalidOperationException($"expected to open {asker} -> {answerer}");

        await _messages.AppendAsync(
            exchange.Id, MessageType.Question, asker, answerer, "test-model",
            JsonSerializer.Serialize(new
            {
                text = question,

                // §8 rule 1, enforced by a CHECK with a length floor rather than a key-present
                // test, because "n/a" satisfies the second and states nothing.
                consequences = "If the answer is yes I read it from you; if not I define my own."
            }),
            T0,
            cancellationToken: ct);

        return exchange;
    }

    private async Task<ExchangeState> StateOf(long id) =>
        (await _exchanges.GetAsync(id, cancellationToken: TestContext.Current.CancellationToken))!.State;

    private async Task<AgentStatus> StatusOf(AgentName name) =>
        (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == name).Status;

    // ---- the reboot sequence ----

    [Fact]
    public async Task Attaching_orphans_what_that_agent_owes()
    {
        // A debt whose worker just vanished keeps a deadline nobody is working towards, and a
        // wait-for edge blocking peers on behalf of a process that is not running.
        var owed = await Open(Orders, Billing, "q1");

        var result = await NewBroker().StartAsync(
            Billing, T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExchangesOrphaned);
        Assert.Equal(ExchangeState.Orphaned, await StateOf(owed.Id));
    }

    [Fact]
    public async Task Attaching_leaves_a_peers_live_work_alone()
    {
        // The property the scoping exists for, and the one whose absence was silent. Billing is
        // waiting on inventory for the second exchange — inventory may well be mid-answer, so
        // orphaning it would discard a peer's work because a process on the other side of the
        // conversation restarted.
        var owed = await Open(Orders, Billing, "q1");
        var waitingOn = await Open(Billing, Inventory, "q2");

        var result = await NewBroker().StartAsync(
            Billing, T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExchangesOrphaned);
        Assert.Equal(ExchangeState.Orphaned, await StateOf(owed.Id));
        Assert.Equal(ExchangeState.Open, await StateOf(waitingOn.Id));
    }

    [Fact]
    public async Task One_agent_attaching_does_not_disturb_another()
    {
        // Twenty-two repositories with an agent apiece is the deployment §1 describes, and under
        // the broker-wide sequence the second session to open orphaned the first's work, the
        // third orphaned both, and none of them could tell. Two is enough to show it.
        // Three agents, not two: orders owing billing and billing owing orders would close a
        // 2-cycle, which §7.2 refuses at admission. Which is itself the point — the debts in a
        // real deployment fan out rather than pair up.
        var ordersOwes = await Open(Billing, Orders, "for orders");
        var billingOwes = await Open(Inventory, Billing, "for billing");

        await NewBroker().StartAsync(Orders, T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeState.Orphaned, await StateOf(ordersOwes.Id));
        Assert.Equal(ExchangeState.Open, await StateOf(billingOwes.Id));

        // And billing attaching later takes its own, still leaving orders' alone.
        await NewBroker().StartAsync(Billing, T0.AddHours(2), TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeState.Orphaned, await StateOf(billingOwes.Id));
    }

    [Fact]
    public async Task Attaching_marks_that_agent_live_and_nobody_else()
    {
        // An unreachable mark clears by the agent proving it wrong, and attaching is that proof.
        // Better evidence than the old broker restart, which cleared the mark on an agent
        // nobody had heard from.
        GoSilent(Orders);

        var result = await NewBroker().StartAsync(
            Orders, T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AgentMarkedLive);
        Assert.Equal(AgentStatus.Active, await StatusOf(Orders));
        Assert.Equal(AgentStatus.Active, await StatusOf(Billing));
    }

    [Fact]
    public async Task Attaching_does_not_revive_a_retired_agent()
    {
        // The one judgement an attach is not evidence against. §14: retirement is cleared only
        // by an explicit operator action, never by registration — and an agent attaching is the
        // closest thing to registration there is, since it is the agent announcing itself.
        // Without the guard, an operator who retired an agent would find it live again the
        // moment somebody opened that repository's session.
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        var result = await NewBroker().StartAsync(
            Orders, T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.AgentMarkedLive);
        Assert.Equal(AgentStatus.Retired, await StatusOf(Orders));
    }

    [Fact]
    public async Task Attaching_to_a_quiet_deployment_does_nothing_surprising()
    {
        var result = await NewBroker().StartAsync(Orders, T0, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExchangesOrphaned);
        Assert.Equal(1, result.AgentMarkedLive);
    }

    [Fact]
    public async Task Orphaning_at_start_up_starts_the_grace_period_from_then()
    {
        // Not from when the exchange was opened. An exchange interrupted by a restart has not
        // been abandoned by anybody yet, so its grace period begins when the broker returns.
        await Open(Orders, Billing, "q1");

        var brokerStart = T0.AddDays(3);
        var broker = NewBroker();
        await broker.StartAsync(Billing, brokerStart, TestContext.Current.CancellationToken);

        var beforeGrace = (await broker.TickAsync(
            brokerStart + ExchangePolicy.Default.GracePeriod - TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken)).Exchanges;
        Assert.Equal(0, beforeGrace.Ghosted);

        var afterGrace = (await broker.TickAsync(
            brokerStart + ExchangePolicy.Default.GracePeriod,
            TestContext.Current.CancellationToken)).Exchanges;
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
        await broker.StartAsync(Billing, T0, TestContext.Current.CancellationToken);

        var ghostingDue = T0 + ExchangePolicy.Default.GracePeriod;
        Assert.Equal(1, ((await broker.TickAsync(
            ghostingDue, TestContext.Current.CancellationToken)).Exchanges).Ghosted);

        // A second exchange, a second start, and the clock restarts from there.
        await Open(Orders, Billing, "q2");
        var secondStart = T0.AddDays(1);
        // The previous session stopped cleanly, which is what marks the agent offline and so
        // what tells the next attach that no worker is still holding these. Without it the
        // agent is still live and §14 declines to release anything — the right answer for a
        // second window opened alongside a live session, and the wrong one here.
        await broker.DetachAsync(TestContext.Current.CancellationToken);
        await broker.StartAsync(Billing, secondStart, TestContext.Current.CancellationToken);

        Assert.Equal(secondStart, broker.StartedUtc);
        Assert.Equal(0, ((await broker.TickAsync(
            secondStart + ExchangePolicy.Default.GracePeriod - TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken)).Exchanges).Ghosted);
        Assert.Equal(1, ((await broker.TickAsync(
            secondStart + ExchangePolicy.Default.GracePeriod,
            TestContext.Current.CancellationToken)).Exchanges).Ghosted);
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
        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);

        var exchange = await Open(Orders, Billing, "q1");

        var result = (await broker.TickAsync(
            T0 + ExchangePolicy.Default.DeliveryWindow, TestContext.Current.CancellationToken)).Exchanges;

        Assert.Equal(1, result.Orphaned);

        var loaded = await _exchanges.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeState.Orphaned, loaded!.State);
    }

    [Fact]
    public async Task The_broker_records_when_it_started()
    {
        var broker = NewBroker();
        Assert.Null(broker.StartedUtc);

        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);

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
            () => broker.StartAsync(Orders, T0, cancelled.Token));

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
        await broker.StartAsync(Billing, T0, TestContext.Current.CancellationToken);

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

    /// <summary>Takes an agent's machine off the air, the way a lost node does.</summary>
    /// <param name="agent">The agent to strand.</param>
    /// <remarks>
    /// This used to age a heartbeat into the past, because unreachability was arithmetic on a
    /// timestamp and there was no other way to produce one. It is now an observation with a
    /// writer -- the node's connection closed -- so the test performs the same write the broker
    /// does, and there is nothing left to simulate.
    /// </remarks>
    private void GoSilent(AgentName agent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE agents SET connected = 0 WHERE name = @Name;
            UPDATE nodes  SET connected = 0
             WHERE machine = (SELECT machine FROM agents WHERE name = @Name);
            """;
        command.Parameters.AddWithValue("@Name", agent.Value);
        command.ExecuteNonQuery();
    }

}
