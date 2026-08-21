using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What the per-agent attach sequence does at its edges.
/// </summary>
/// <remarks>
/// The scoping in SPEC.md §14 keys on <c>owed_by</c>, which moves during a conversation, so
/// which agent's attach releases an exchange is not fixed when it is opened. These pin the
/// consequences of that, and two properties that would be easy to lose: a restart must not
/// extend a grace period, and a retirement must not strand the peers waiting on it.
/// </remarks>
public sealed class AttachEdgeTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Model = "claude-opus-5";

    private const string Question =
        """{"text":"gross or net?","consequences":"If gross I stop adding tax; if net I keep it."}""";

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;

    public AttachEdgeTests()
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

    private async Task<ExchangeState> StateOf(long id) =>
        (await _exchanges.GetAsync(id, cancellationToken: TestContext.Current.CancellationToken))!.State;

    private async Task<AgentStatus> StatusOf(AgentName name) =>
        (await _agents.ListRoutableAsync(TestContext.Current.CancellationToken))
            .SingleOrDefault(a => a.Name == name)?.Status ?? AgentStatus.Retired;

    [Fact]
    public async Task A_clarify_moves_which_agents_attach_releases_the_exchange()
    {
        // owed_by flips on a clarify. So the agent whose attach orphans an exchange changes
        // mid-conversation, and an agent that attaches while holding no debt orphans nothing —
        // even for an exchange it is very much party to.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);

        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        // Billing asks for detail, so the debt is now orders'.
        // Each side collects what it was sent before acting on it, which is what a real session
        // does and what §14 now requires before a debt counts as abandoned.
        await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);

        await broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, """{"text":"gross of what?"}""",
            T0.AddMinutes(2), TestContext.Current.CancellationToken);

        await broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);

        var billingAttach = await new Broker(_connection, BrokerOptions.Default)
            .StartAsync(Billing, T0.AddMinutes(3), TestContext.Current.CancellationToken);
        var afterBilling = (await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken))!.State;

        // The previous session stopped cleanly, which is what marks the agent offline and so
        // what tells the next attach that no worker is still holding these. Without it the
        // agent is still live and §14 declines to release anything — the right answer for a
        // second window opened alongside a live session, and the wrong one here.
        await broker.DetachAsync(TestContext.Current.CancellationToken);

        var ordersAttach = await new Broker(_connection, BrokerOptions.Default)
            .StartAsync(Orders, T0.AddHours(2), TestContext.Current.CancellationToken);
        var afterOrders = (await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken))!.State;

        // Billing holds no debt any more, so its attach releases nothing — even though it is
        // very much party to this exchange.
        Assert.Equal(0, billingAttach.ExchangesOrphaned);
        Assert.Equal(ExchangeState.Open, afterBilling);

        Assert.Equal(1, ordersAttach.ExchangesOrphaned);
        Assert.Equal(ExchangeState.Orphaned, afterOrders);
    }

    [Fact]
    public async Task Attaching_twice_does_not_extend_a_grace_period()
    {
        // The second attach reads open exchanges only, so an orphan from the first should be
        // left alone — its expires_utc is the grace clock and re-stamping it would extend the
        // grace period every time a session restarted.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);
        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        await new Broker(_connection, BrokerOptions.Default)
            .StartAsync(Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);
        var first = (await _exchanges.GetAsync(
            asked.Exchange!.Id, cancellationToken: TestContext.Current.CancellationToken))!.ExpiresUtc;

        var second = await new Broker(_connection, BrokerOptions.Default)
            .StartAsync(Billing, T0.AddHours(9), TestContext.Current.CancellationToken);
        var after = (await _exchanges.GetAsync(
            asked.Exchange.Id, cancellationToken: TestContext.Current.CancellationToken))!.ExpiresUtc;

        // Nothing re-orphaned, and the stamp the grace period is measured from is untouched.
        // Re-stamping it would hand an agent a fresh grace period every time its session
        // restarted, which is a way to never be ghosted at all.
        Assert.Equal(0, second.ExchangesOrphaned);
        Assert.Equal(first, after);
    }

    [Fact]
    public async Task Detaching_marks_the_agent_offline_and_leaves_its_debts()
    {
        // Detaching says the session stopped, and nothing more. The debts stay open on purpose:
        // whoever picks the session up next may finish them, and the attach sequence releases
        // them on the way back in — which is the moment it is actually known that nobody did.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, T0, TestContext.Current.CancellationToken);

        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await broker.DetachAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AgentStatus.Offline, await StatusOf(Billing));
        Assert.Equal(ExchangeState.Open, await StateOf(asked.Exchange!.Id));

        // And an offline agent is still routable, because offline is the resting state of a
        // lazily spawned agent rather than a fault.
        var stillReachable = await broker.AskAsync(
            Orders, Billing, Model,
            """{"text":"and duty?","consequences":"If included I stop adding it; if not I add it."}""",
            T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(stillReachable.Succeeded, $"expected admission, got {stillReachable.Rejection}");
    }

    [Fact]
    public async Task Detaching_does_not_withdraw_a_retirement()
    {
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);

        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, T0, TestContext.Current.CancellationToken);

        Assert.False(await broker.DetachAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentStatus.Retired, await StatusOf(Billing));
    }

    [Fact]
    public async Task Detaching_before_attaching_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Broker(_connection, BrokerOptions.Default)
                .DetachAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Attaching_as_an_agent_nobody_registered_is_refused()
    {
        // A deployment fault, not an agent one, so it throws where an unroutable peer returns a
        // refusal. Found by probe: it previously attached cleanly, orphaned nothing, marked
        // nothing live, and then threw on every operation — reporting the misconfiguration to
        // the model rather than to whoever wrote the launch configuration.
        //
        // It also keeps StartResult.AgentMarkedLive unambiguous. A zero now means retired, and
        // only retired, which is what an adapter reads to tell "withdrawn" from "working".
        var attaching = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Broker(_connection, BrokerOptions.Default).StartAsync(
                AgentName.Create("ghost"), T0, TestContext.Current.CancellationToken));

        Assert.Contains("ghost", attaching.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sweep_by_one_agent_still_disposes_of_anothers_abandoned_debt()
    {
        // Every running server sweeps, and the sweep stays broker-wide on purpose. That is what
        // keeps §6 and §14 enforced while the owing agent is not running at all — the case that
        // becomes the common one once agents are sessions somebody opens.
        var orders = new Broker(_connection, BrokerOptions.Default);
        await orders.StartAsync(Orders, T0, TestContext.Current.CancellationToken);

        var asked = await orders.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, (await orders.TickAsync(
            T0.AddHours(1), TestContext.Current.CancellationToken)).Exchanges.Orphaned);
        Assert.Equal(1, (await orders.TickAsync(
            T0.AddDays(1), TestContext.Current.CancellationToken)).Exchanges.Ghosted);

        var loaded = await _exchanges.GetAsync(
            asked.Exchange!.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Core.Resolution.Ghosted, loaded!.Resolution);
    }

    [Fact]
    public async Task A_retired_agent_attaching_still_releases_its_debts()
    {
        // Retirement stops routing. It should not stop the debts of a retired agent being
        // released — a peer waiting on an agent an operator withdrew should not wait forever.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);
        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);

        var result = await new Broker(_connection, BrokerOptions.Default)
            .StartAsync(Billing, T0.AddMinutes(2), TestContext.Current.CancellationToken);
        var state = (await _exchanges.GetAsync(
            asked.Exchange!.Id, cancellationToken: TestContext.Current.CancellationToken))!.State;

        // Not marked live, because an operator withdrew it — but its debts are still released,
        // because a peer waiting on a withdrawn agent should not wait forever.
        Assert.Equal(0, result.AgentMarkedLive);
        Assert.Equal(1, result.ExchangesOrphaned);
        Assert.Equal(ExchangeState.Orphaned, state);
    }
}
