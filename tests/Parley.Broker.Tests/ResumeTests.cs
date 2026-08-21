using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What a restart does to the exchanges it interrupted.
/// </summary>
/// <remarks>
/// The decision this covers is that there is no correct answer to infer. Two restarts that are
/// byte-identical to the broker carry opposite intent: an operator killing the process to stop
/// two agents burning a quota, and a machine taking a forced update at the weekend. Resuming
/// overrides the first; dropping the work wastes the second. So these pin that the broker asks
/// what it was told rather than deciding.
/// </remarks>
public sealed class ResumeTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;

    public ResumeTests()
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
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Opens an exchange, then restarts the broker so it is orphaned and owed.</summary>
    private async Task<Broker> InterruptedBy(BrokerOptions options, params string[] questions)
    {
        foreach (var question in questions)
        {
            var opened = await _exchanges.TryOpenAsync(
                Orders, Billing, T0, question,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(opened.Succeeded, $"expected to open '{question}', got {opened.Rejection}");
        }

        var broker = new Broker(_connection, options);
        await broker.StartAsync(T0.AddMinutes(1), TestContext.Current.CancellationToken);
        return broker;
    }

    private static BrokerOptions With(ResumePolicy policy, IResumeGate? gate = null) =>
        BrokerOptions.Default with
        {
            ResumePolicy = policy,
            ResumeGate = gate ?? AlwaysResume.Instance
        };

    [Fact]
    public async Task Always_replays_what_the_agent_owes()
    {
        var broker = await InterruptedBy(With(ResumePolicy.Always), "q1", "q2");

        var resumed = await broker.ResumeOwedByAsync(
            Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, resumed.Count);
        Assert.All(resumed, e => Assert.Equal(ExchangeState.Open, e.State));
    }

    [Fact]
    public async Task Never_replays_nothing_and_leaves_the_orphans_in_place()
    {
        // Retained rather than discarded: the operator may still resume by hand, and the grace
        // period bounds how long that window stays open.
        var broker = await InterruptedBy(With(ResumePolicy.Never), "q1");

        var resumed = await broker.ResumeOwedByAsync(
            Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Empty(resumed);
        Assert.Single(await _exchanges.GetOrphanedOwedByAsync(
            Billing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ask_declines_rather_than_inventing_consent()
    {
        // Somebody must be asked, and asking is an operator surface the broker does not have.
        // Degrading to "not yet" is the safe direction: the orphan stays, and the grace period
        // eventually ghosts it, so nothing waits indefinitely on a question nobody answered.
        var broker = await InterruptedBy(With(ResumePolicy.Ask), "q1");

        var resumed = await broker.ResumeOwedByAsync(
            Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Empty(resumed);
        Assert.Single(await _exchanges.GetOrphanedOwedByAsync(
            Billing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_gate_can_decline_a_replay_the_policy_permits()
    {
        // The gate sits beyond the policy: always says what the operator wants, the gate says
        // whether this moment allows it.
        var broker = await InterruptedBy(With(ResumePolicy.Always, new RefusingGate()), "q1");

        var resumed = await broker.ResumeOwedByAsync(
            Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Empty(resumed);
    }

    [Fact]
    public async Task The_gate_is_asked_once_per_exchange()
    {
        // A budget gate that has just permitted several replays may reasonably decline the next,
        // which it cannot do if it is consulted once for the batch.
        var gate = new CountingGate(permitFirst: 2);
        var broker = await InterruptedBy(With(ResumePolicy.Always, gate), "q1", "q2", "q3");

        var resumed = await broker.ResumeOwedByAsync(
            Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Equal(3, gate.Asked);
        Assert.Equal(2, resumed.Count);
    }

    [Fact]
    public async Task The_gate_is_not_consulted_when_the_policy_already_said_no()
    {
        // Never means the operator has decided. Asking a budget guard for permission to do
        // something nobody asked for would burn whatever the guard costs to consult.
        var gate = new CountingGate(permitFirst: int.MaxValue);
        var broker = await InterruptedBy(With(ResumePolicy.Never, gate), "q1");

        await broker.ResumeOwedByAsync(Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Equal(0, gate.Asked);
    }

    [Fact]
    public async Task A_replay_restarts_the_delivery_window()
    {
        // The question is undelivered again, so the exchange is waiting for pickup rather than
        // for an answer. Granting the working window here would give an agent that never
        // returned four hours of silence instead of fifteen minutes.
        var broker = await InterruptedBy(With(ResumePolicy.Always), "q1");
        var resumedAt = T0.AddMinutes(2);

        var resumed = await broker.ResumeOwedByAsync(
            Billing, resumedAt, TestContext.Current.CancellationToken);

        Assert.Equal(resumedAt + ExchangePolicy.Default.DeliveryWindow, Assert.Single(resumed).ExpiresUtc);
    }

    [Fact]
    public async Task A_replay_preserves_the_clarify_depth()
    {
        // A restart is not a fresh budget for going in circles.
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "q1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(opened.Exchange!.Clarify(Billing, T0.AddSeconds(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            opened.Exchange, cancellationToken: TestContext.Current.CancellationToken));

        var broker = new Broker(_connection, With(ResumePolicy.Always));
        await broker.StartAsync(T0.AddMinutes(1), TestContext.Current.CancellationToken);

        // The clarify flipped the debt to orders, so orders is who owes the response.
        var resumed = await broker.ResumeOwedByAsync(
            Orders, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(resumed).ClarifyDepth);
    }

    [Fact]
    public async Task Resuming_asks_only_for_what_the_named_agent_owes()
    {
        var broker = await InterruptedBy(With(ResumePolicy.Always), "q1");

        var wrongWay = await broker.ResumeOwedByAsync(
            Orders, T0.AddMinutes(2), TestContext.Current.CancellationToken);

        Assert.Empty(wrongWay);
    }

    [Fact]
    public async Task A_replay_whose_question_was_re_asked_does_not_take_the_broker_down()
    {
        // The collision SPEC 17 open question 9 describes. An asker may re-ask while the
        // original is orphaned, and the distinctness index refuses the replay. The question is
        // what that refusal looks like from here.
        var broker = await InterruptedBy(With(ResumePolicy.Always), "where does price live");

        var reAsked = await _exchanges.TryOpenAsync(
            Orders, Billing, T0.AddMinutes(2), "where does price live",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(reAsked.Succeeded, $"expected the re-ask to be admitted, got {reAsked.Rejection}");

        var resumed = await broker.ResumeOwedByAsync(
            Billing, T0.AddMinutes(3), TestContext.Current.CancellationToken);

        // The re-ask survives and the orphan stays orphaned for the grace period to deal with.
        Assert.Empty(resumed);
    }

    [Fact]
    public async Task A_gate_that_throws_surfaces_rather_than_being_absorbed()
    {
        // Decided rather than accidental, and the opposite of how a constraint violation is
        // handled two lines away. A violation is an expected outcome of ordinary operation, so
        // the pass continues; a gate is the host's own code, so a throw is a defect it needs to
        // see. A broker that swallowed it would leave replays silently not happening.
        var broker = await InterruptedBy(With(ResumePolicy.Always, new ThrowingGate()), "q1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.ResumeOwedByAsync(
                Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken));
    }

    private sealed class ThrowingGate : IResumeGate
    {
        public ValueTask<bool> MayResumeAsync(
            Exchange exchange, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the budget service is unreachable");
    }

    [Fact]
    public async Task A_declined_replay_leaves_the_question_out_of_the_inbox()
    {
        // Re-delivery happens after the gate, not before. A gate that declines must leave the
        // exchange exactly as it found it — reopening nothing and handing back nothing — or the
        // agent would see a question for an exchange still orphaned.
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "q1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(opened.Succeeded);

        var messages = new MessageRepository(_connection);
        var appended = await messages.AppendAsync(
            opened.Exchange!.Id, MessageType.Question, Orders, Billing, "claude-opus-5",
            """{"text":"gross or net?","consequences":"If gross I stop adding tax; if net I keep it."}""",
            T0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(appended.Stored);

        var broker = new Broker(_connection, With(ResumePolicy.Always, new RefusingGate()));
        await broker.StartAsync(T0.AddMinutes(1), TestContext.Current.CancellationToken);
        Assert.True(await messages.MarkDeliveredAsync(
            appended.Message.Id, T0.AddMinutes(1), TestContext.Current.CancellationToken));

        var drained = (await broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken)).Messages;

        Assert.Empty(drained);
    }

    private sealed class RefusingGate : IResumeGate
    {
        public ValueTask<bool> MayResumeAsync(
            Exchange exchange, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class CountingGate(int permitFirst) : IResumeGate
    {
        public int Asked { get; private set; }

        public ValueTask<bool> MayResumeAsync(
            Exchange exchange, CancellationToken cancellationToken = default)
        {
            Asked++;
            return ValueTask.FromResult(Asked <= permitFirst);
        }
    }
}
