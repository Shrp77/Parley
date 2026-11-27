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
/// These are about sequencing rather than about the transitions themselves: that an agent coming back is given
/// the work it had collected, that a retirement outlives an attach, and that nothing runs before the broker
/// knows it has started.
/// </para>
/// <para>
/// <b>Nothing here ends work.</b> Attaching does not orphan what the agent owed and the tick does not ghost it, since
/// either would destroy Requests an agent had not had a turn to read. An exchange stays open until its receiver
/// sends a Result or its conversation is closed, so the sequence scoped to the attaching agent hands work back
/// rather than taking it away.
/// </para>
/// </remarks>
[Integration]
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
        Requests.Register(_connection, Orders, Billing, Inventory);

        _agents = new AgentRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Broker NewBroker(BrokerOptions? options = null) => new(_connection, options);

    /// <summary>Sends a Request through a broker and has the receiver collect it, as a session would.</summary>
    private async Task<Exchange> Collected(AgentName from, AgentName to, string text)
    {
        // Served rather than attached as the sender: an attach is itself the sequence under test, and one here
        // would hand the sender back its own collected work before the test began.
        var sender = NewBroker();
        sender.ServeFrom(T0);

        var sent = await sender.RequestAsync(from, to, Requests.Model, Requests.Request(text), T0, cancellationToken: Ct);
        Assert.True(sent.Succeeded, $"expected to send {from} -> {to}, got {sent.Rejection}");

        await _messages.MarkDeliveredAsync(sent.Request!.Id, T0, Ct);

        return sent.Exchange!;
    }

    private async Task<AgentStatus> StatusOf(AgentName name) =>
        (await _agents.GetAllAsync(Ct)).Single(a => a.Name == name).Status;

    // ---- attaching ----

    [Fact]
    public async Task Attaching_hands_back_what_that_agent_collected_and_left_open()
    {
        var owed = await Collected(Orders, Billing, "q1");

        var result = await NewBroker().StartAsync(Billing, T0.AddHours(1), Ct);

        Assert.Equal(1, result.ExchangesRedelivered);
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(owed.Id, cancellationToken: Ct))!.State);
        Assert.Single(await _messages.GetPendingAsync(Billing, cancellationToken: Ct));
    }

    [Fact]
    public async Task Attaching_leaves_work_the_agent_is_waiting_on_alone()
    {
        // Billing is waiting on inventory for the second exchange. Inventory may be mid-way through it, and a
        // process on the other side restarting is no evidence about that.
        await Collected(Orders, Billing, "q1");
        await Collected(Billing, Inventory, "q2");

        var result = await NewBroker().StartAsync(Billing, T0.AddHours(1), Ct);

        Assert.Equal(1, result.ExchangesRedelivered);
        Assert.Empty(await _messages.GetPendingAsync(Inventory, cancellationToken: Ct));
    }

    [Fact]
    public async Task One_agent_attaching_does_not_disturb_another()
    {
        await Collected(Billing, Orders, "for orders");
        await Collected(Inventory, Billing, "for billing");

        await NewBroker().StartAsync(Orders, T0.AddHours(1), Ct);

        Assert.Single(await _messages.GetPendingAsync(Orders, cancellationToken: Ct));
        Assert.Empty(await _messages.GetPendingAsync(Billing, cancellationToken: Ct));
    }

    [Fact]
    public async Task Attaching_marks_that_agent_live_and_nobody_else()
    {
        var result = await NewBroker().StartAsync(Orders, T0.AddHours(1), Ct);

        Assert.Equal(1, result.AgentMarkedLive);
        Assert.True(await _agents.IsAttachedAsync(Orders, cancellationToken: Ct));
        Assert.False(await _agents.IsAttachedAsync(Billing, cancellationToken: Ct));
    }

    [Fact]
    public async Task Attaching_does_not_revive_a_retired_agent_or_hand_it_work()
    {
        // §14: retirement is cleared only by the person, never by the agent announcing itself. Work it holds
        // stays open and is flagged as held by a retired agent, for the person to close.
        await Collected(Orders, Billing, "q1");
        await _agents.RetireAsync(Billing, Ct);

        var result = await NewBroker().StartAsync(Billing, T0.AddHours(1), Ct);

        Assert.Equal(0, result.AgentMarkedLive);
        Assert.Equal(0, result.ExchangesRedelivered);
        Assert.Equal(AgentStatus.Retired, await StatusOf(Billing));
        Assert.Equal(FlagKind.Retired, Assert.Single((await new FlagRepository(_connection).ReadAsync(T0.AddHours(1), Ct)).Exchanges).Kind);
    }

    [Fact]
    public async Task Attaching_to_a_quiet_deployment_does_nothing_surprising()
    {
        var result = await NewBroker().StartAsync(Orders, T0, Ct);

        Assert.Equal(0, result.ExchangesRedelivered);
        Assert.Equal(1, result.AgentMarkedLive);
    }

    [Fact]
    public async Task The_broker_records_when_it_started()
    {
        var broker = NewBroker();
        Assert.Null(broker.StartedUtc);

        await broker.StartAsync(Orders, T0, Ct);

        Assert.Equal(T0, broker.StartedUtc);
        Assert.Equal(Orders, broker.Attached);
    }

    [Fact]
    public async Task A_start_that_is_cancelled_part_way_leaves_the_broker_unstarted()
    {
        await Collected(Orders, Billing, "q1");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var broker = NewBroker();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broker.StartAsync(Billing, T0, cancelled.Token));

        Assert.Null(broker.StartedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.TickAsync(T0, Ct));
    }

    // ---- the tick ----

    [Fact]
    public async Task Ticking_before_starting_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBroker().TickAsync(T0, Ct));

        Assert.Contains("StartAsync", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticking_ends_nothing_however_long_work_has_waited()
    {
        // There is no exchange sweep. Silence is flagged to the person and changes nothing (principle 2.8).
        var owed = await Collected(Orders, Billing, "q1");

        var broker = NewBroker();
        await broker.StartAsync(Inventory, T0, Ct);
        await broker.TickAsync(T0.AddYears(1), Ct);

        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(owed.Id, cancellationToken: Ct))!.State);
    }

    [Fact]
    public async Task Ticking_closes_a_poll_whose_last_holdout_was_retired()
    {
        // A poll everybody answered closes on the last vote. What only the tick notices is a registry change:
        // the one participant not done was withdrawn by the person.
        var broker = NewBroker();
        await broker.StartAsync(Orders, T0, Ct);

        // Billing still holds the Request, so it is polled, and its silence blocks.
        await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0, "pricing", Ct);

        Assert.False((await broker.ConcludeAsync("pricing", Orders, true, T0.AddMinutes(2), Ct)).Concluded);

        await _agents.RetireAsync(Billing, Ct);

        Assert.Equal(1, (await broker.TickAsync(T0.AddMinutes(3), Ct)).ConversationsConcluded);
    }

    // ---- waking for outstanding work (§14) ----

    [Fact]
    public async Task By_default_nothing_is_woken_and_the_work_stays_open()
    {
        // The policy decides only whether agents are woken, never whether the work survives.
        await Collected(Orders, Billing, "q1");

        Assert.Equal(ResumePolicy.Never, BrokerOptions.Default.ResumePolicy);
        Assert.Empty(await NewBroker().OutstandingToWakeForAsync(Billing, Ct));
    }

    [Fact]
    public async Task Under_always_the_work_an_agent_owes_is_what_it_is_woken_for()
    {
        var owed = await Collected(Orders, Billing, "q1");
        await Collected(Billing, Inventory, "q2");

        var outstanding = await NewBroker(BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always })
            .OutstandingToWakeForAsync(Billing, Ct);

        Assert.Equal(owed.Id, Assert.Single(outstanding).Id);
    }

    [Fact]
    public async Task A_gate_that_declines_keeps_an_agent_asleep_without_touching_the_work()
    {
        var owed = await Collected(Orders, Billing, "q1");

        var outstanding = await NewBroker(BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always, ResumeGate = new Declining() })
            .OutstandingToWakeForAsync(Billing, Ct);

        Assert.Empty(outstanding);
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(owed.Id, cancellationToken: Ct))!.State);
    }

    private sealed class Declining : IResumeGate
    {
        public ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    // ---- configuration ----

    [Fact]
    public void A_broker_needs_a_connection()
    {
        Assert.Throws<ArgumentNullException>(() => new Broker(null!));
    }

    [Fact]
    public void A_policy_storage_would_refuse_is_caught_at_construction()
    {
        // Rather than on the first Request, with a constraint violation several layers below the mistake.
        var options = BrokerOptions.Default with { ExchangePolicy = ExchangePolicy.Default with { StalledAfter = TimeSpan.Zero } };

        Assert.Throws<ArgumentOutOfRangeException>(() => NewBroker(options));
    }

    [Fact]
    public void An_undefined_resume_policy_is_caught_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewBroker(BrokerOptions.Default with { ResumePolicy = (ResumePolicy)99 }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_missing_member_of_the_configuration_is_refused(bool nullPolicy)
    {
        var options = nullPolicy
            ? BrokerOptions.Default with { ExchangePolicy = null! }
            : BrokerOptions.Default with { ResumeGate = null! };

        Assert.Throws<ArgumentNullException>(() => NewBroker(options));
    }
}
