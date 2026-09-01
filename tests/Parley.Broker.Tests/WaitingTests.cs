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
/// The read exists so an operator can see a discussion nobody is going to finish. From inside a
/// pair the two states are identical — a question that will never be answered looks exactly like
/// one that has not been answered yet — so this is only ever true if it is derived correctly, and
/// there is nothing on the page a reader could check it against.
/// </para>
/// <para>
/// The case that makes it a derivation rather than a lookup is clarification. Before one, the
/// asker waits on the answerer; after one, the answerer has asked back and it is the original
/// asker who owes. Reading the asker as the waiter is right most of the time and points the arrow
/// exactly backwards in the case an operator opened the page to look at.
/// </para>
/// </remarks>
public sealed class WaitingTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";

    private const string Question =
        """{"text":"is the price gross or net?","consequences":"If gross I stop adding tax; if net I keep adding it."}""";

    private const string Clarification =
        """{"text":"which price, the line or the order?","consequences":"The line total is taxed differently from the order, so I would answer the wrong one."}""";

    private const string Answer =
        """{"text":"net","consequences":"Keep adding tax at the line."}""";

    private readonly SqliteConnection _connection;
    private readonly Broker _broker;
    private readonly OperatorConsole _console;

    public WaitingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
                Ct).GetAwaiter().GetResult();
        }

        _broker = new Broker(_connection, BrokerOptions.Default);
        _console = new OperatorConsole(_connection);

        _broker.StartAsync(Orders, T0, Ct).GetAwaiter().GetResult();
        _broker.StartAsync(Billing, T0, Ct).GetAwaiter().GetResult();
        _broker.StartAsync(Inventory, T0, Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_deployment_that_has_done_nothing_has_nobody_waiting()
    {
        Assert.Empty(await _console.DescribeWaitingAsync(Ct));
    }

    [Fact]
    public async Task The_asker_waits_on_the_answerer()
    {
        var asked = await _broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1), null, Ct);

        Assert.True(asked.Succeeded);

        var edge = Assert.Single(await _console.DescribeWaitingAsync(Ct));

        Assert.Equal(Orders, edge.Waiter);
        Assert.Equal(Billing, edge.WaitedOn);
        Assert.Equal(asked.Exchange!.Id, edge.ExchangeId);
    }

    [Fact]
    public async Task A_clarification_turns_the_arrow_round()
    {
        // The case worth having. The answerer has asked back, so the original asker owes the next
        // move — and an operator looking for who is holding things up would be sent to precisely
        // the wrong agent by a derivation that read the asker as the waiter.
        var asked = await _broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1), null, Ct);

        await _broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, Clarification, T0.AddMinutes(2), Ct);

        var edge = Assert.Single(await _console.DescribeWaitingAsync(Ct));

        Assert.Equal(Billing, edge.Waiter);
        Assert.Equal(Orders, edge.WaitedOn);
    }

    [Fact]
    public async Task An_answered_exchange_leaves_nobody_waiting()
    {
        // Derived from the open set rather than stored, so a closed exchange simply is not in the
        // next read. A stored edge is one somebody has to remember to remove.
        var asked = await _broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1), null, Ct);

        await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model, Answer, T0.AddMinutes(3),
            Resolution.Answered, false, Ct);

        Assert.Empty(await _console.DescribeWaitingAsync(Ct));
    }

    [Fact]
    public async Task Every_open_exchange_gets_an_edge_of_its_own()
    {
        // One edge per open exchange rather than one per agent. An agent can be waited on by two
        // peers at once, and collapsing that would hide the second.
        // Deliberately not orders->billing and billing->orders: that pair would close a wait
        // cycle and the broker refuses it, which is the deadlock guard working rather than a
        // scenario worth writing.
        var first = await _broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1), null, Ct);

        var second = await _broker.AskAsync(
            Inventory, Billing, Model, Question, T0.AddMinutes(2), null, Ct);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        var edges = await _console.DescribeWaitingAsync(Ct);

        Assert.Equal(2, edges.Count);
        Assert.Equal(2, edges.Select(e => e.ExchangeId).Distinct().Count());
    }
}
