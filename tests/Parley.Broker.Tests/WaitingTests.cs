using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Who is waiting on whom, and the half of it that reverses when somebody asks back.
/// </summary>
/// <remarks>
/// <para>
/// The read exists so the person can see work nobody is going to finish. From inside a pair the two states are
/// identical — work that will never be done looks exactly like work not done yet — so this is only ever true if
/// it is derived correctly, and there is nothing on the page a reader could check it against.
/// </para>
/// <para>
/// The case that makes it a derivation rather than a lookup is clarification. Before one, the requester waits
/// on the receiver; after one, the receiver has asked back and it is the requester who owes. Reading the
/// requester as the waiter is right most of the time and points the arrow exactly backwards in the case the
/// person opened the page to look at.
/// </para>
/// </remarks>
public sealed class WaitingTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly Broker _broker;
    private readonly OperatorConsole _console;

    public WaitingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing, Inventory);

        _broker = new Broker(_connection, BrokerOptions.Default);
        _console = new OperatorConsole(_connection);

        _broker.StartAsync(Orders, T0, Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<RequestOutcome> Send(AgentName from, AgentName to, int minute) =>
        _broker.RequestAsync(from, to, Requests.Model, Requests.Request($"request at {minute}"), T0.AddMinutes(minute), cancellationToken: Ct);

    [Fact]
    public async Task A_deployment_that_has_done_nothing_has_nobody_waiting()
    {
        Assert.Empty(await _console.DescribeWaitingAsync(Ct));
    }

    [Fact]
    public async Task The_requester_waits_on_the_receiver()
    {
        var sent = await Send(Orders, Billing, 1);

        var edge = Assert.Single(await _console.DescribeWaitingAsync(Ct));

        Assert.Equal(Orders, edge.Waiter);
        Assert.Equal(Billing, edge.WaitedOn);
        Assert.Equal(sent.Exchange!.Id, edge.ExchangeId);
    }

    [Fact]
    public async Task A_clarification_turns_the_arrow_round()
    {
        var sent = await Send(Orders, Billing, 1);

        await _broker.ClarifyAsync(
            sent.Exchange!.Id, Billing, Requests.Model, Requests.Clarify("which price, the line or the order?"), T0.AddMinutes(2), Ct);

        var edge = Assert.Single(await _console.DescribeWaitingAsync(Ct));

        Assert.Equal(Billing, edge.Waiter);
        Assert.Equal(Orders, edge.WaitedOn);
    }

    [Fact]
    public async Task Finished_work_leaves_nobody_waiting()
    {
        // Derived from the open set rather than stored, so a closed exchange simply is not in the next read.
        var sent = await Send(Orders, Billing, 1);

        await _broker.ResultAsync(sent.Exchange!.Id, Billing, Requests.Model, Requests.Result(), T0.AddMinutes(3), Ct);

        Assert.Empty(await _console.DescribeWaitingAsync(Ct));
    }

    [Fact]
    public async Task Every_open_exchange_gets_an_edge_of_its_own()
    {
        // One edge per open exchange rather than one per agent. An agent can be waited on by two participants
        // at once, and collapsing that would hide the second.
        Assert.True((await Send(Orders, Billing, 1)).Succeeded);
        Assert.True((await Send(Inventory, Billing, 2)).Succeeded);

        var edges = await _console.DescribeWaitingAsync(Ct);

        Assert.Equal(2, edges.Count);
        Assert.Equal(2, edges.Select(e => e.ExchangeId).Distinct().Count());
    }
}
