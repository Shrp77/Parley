using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What the per-agent attach sequence does at its edges.
/// </summary>
/// <remarks>
/// The sequence keys on <c>owed_by</c>, which moves during an exchange, so which agent's attach hands a piece
/// of work back is not fixed when it is sent. These pin the consequences of that, and two properties easy to
/// lose: a second window must not be handed what the first is working on, and a detach must not end anything.
/// </remarks>
[Integration]
public sealed class AttachEdgeTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;

    public AttachEdgeTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing, Inventory);
        _agents = new AgentRepository(_connection);
        _exchanges = new ExchangeRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AgentPresence> PresenceOf(AgentName name) =>
        (await _agents.ListRoutableAsync(Ct)).SingleOrDefault(a => a.Name == name)?.Presence ?? AgentPresence.Stranded;

    [Fact]
    public async Task A_clarify_moves_which_agents_attach_hands_the_work_back()
    {
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var sent = await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct);

        // Each side collects what it was sent before acting on it, as a real session does.
        await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct);
        await broker.ClarifyAsync(sent.Exchange!.Id, Billing, Requests.Model, Requests.Clarify("gross of what?"), T0.AddMinutes(2), Ct);
        await broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: Ct);

        // Billing holds no move any more, so its attach gives it nothing — even though it is party to the exchange.
        var billingAttach = await new Broker(_connection).StartAsync(Billing, T0.AddMinutes(3), Ct);
        Assert.Equal(0, billingAttach.ExchangesRedelivered);

        await broker.DetachAsync(Ct);

        var ordersAttach = await new Broker(_connection).StartAsync(Orders, T0.AddHours(2), Ct);
        Assert.Equal(1, ordersAttach.ExchangesRedelivered);
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(sent.Exchange.Id, cancellationToken: Ct))!.State);
    }

    [Fact]
    public async Task Detaching_marks_the_agent_offline_and_leaves_its_work_open()
    {
        // Detaching says the session stopped, and nothing more. Whoever picks the session up next finishes the
        // work; if nobody does, the flag puts it in front of the person.
        //
        // The node is joined so that a detached agent reads as Startable rather than Stranded. That is the
        // node's statement to make (SPEC.md §4) and nothing else makes it.
        await new NodeRepository(_connection).JoinAsync(
            new NodeRecord("test", "test-os", null, null, null, Connected: true, T0), T0, Ct);

        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, T0, Ct);

        var sent = await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct);

        Assert.True(await broker.DetachAsync(Ct));

        Assert.Equal(AgentPresence.Startable, await PresenceOf(Billing));
        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(sent.Exchange!.Id, cancellationToken: Ct))!.State);

        // And an offline agent is still routable: offline is the resting state of an agent started on demand.
        var another = await broker.RequestAsync(
            Orders, Billing, Requests.Model, Requests.Request("and duty?"), T0.AddMinutes(2), cancellationToken: Ct);
        Assert.True(another.Succeeded, $"expected admission, got {another.Rejection}");
    }

    [Fact]
    public async Task Detaching_does_not_withdraw_a_retirement()
    {
        await _agents.RetireAsync(Billing, Ct);

        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, T0, Ct);

        Assert.False(await broker.DetachAsync(Ct));
        Assert.Equal(AgentPresence.Stranded, await PresenceOf(Billing));
    }

    [Fact]
    public async Task Detaching_before_attaching_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Broker(_connection).DetachAsync(Ct));
    }

    [Fact]
    public async Task Attaching_as_an_agent_nobody_registered_is_refused()
    {
        // A deployment fault, not an agent one. It also keeps StartResult.AgentMarkedLive unambiguous: a zero
        // means retired, and only retired.
        var attaching = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Broker(_connection).StartAsync(AgentName.Create("nobody@test"), T0, Ct));

        Assert.Contains("nobody", attaching.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_owed_by_nobody_running_stays_open_for_as_long_as_it_takes()
    {
        // The receiver never came back. Nothing closes it on a schedule; it is flagged,
        // and the person closes it or the agent comes back.
        var orders = new Broker(_connection, BrokerOptions.Default);
        await orders.StartAsync(Orders, T0, Ct);

        var sent = await orders.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct);

        await orders.TickAsync(T0.AddHours(1), Ct);
        await orders.TickAsync(T0.AddDays(30), Ct);

        Assert.Equal(ExchangeState.Open, (await _exchanges.GetAsync(sent.Exchange!.Id, cancellationToken: Ct))!.State);

        var flag = Assert.Single((await new FlagRepository(_connection).ReadAsync(T0.AddDays(30), Ct)).Exchanges);
        Assert.Equal(FlagKind.NotCollected, flag.Kind);
        Assert.Equal(Billing, flag.WaitingOn);
    }

    [Fact]
    public async Task A_second_attach_while_the_first_is_live_hands_nothing_back()
    {
        // Two windows on one repository: the second must not re-deliver what the first is working on.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, T0, Ct);
        await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct);
        await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct);

        var second = await new Broker(_connection).StartAsync(Billing, T0.AddHours(9), Ct);

        Assert.Equal(0, second.ExchangesRedelivered);
    }
}
