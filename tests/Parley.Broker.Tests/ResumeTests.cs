using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Whether a restart wakes agents for the work they owe (§14).
/// </summary>
/// <remarks>
/// <para>
/// There is no correct answer to infer. Two restarts that are byte-identical to the broker carry opposite intent:
/// a person killing the process to stop two agents burning a quota, and a machine taking a forced update at the
/// weekend. Waking overrides the first; not waking delays the second. So the broker asks what it was told.
/// </para>
/// <para>
/// <b>Nothing is ghosted.</b> The policy does not decide whether interrupted work is
/// resumed or left to be ghosted: the work stays open and flagged whatever the policy
/// says, and the policy decides only whether an agent is started to do it.
/// </para>
/// </remarks>
[Integration]
public sealed class ResumeTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public ResumeTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing);

        var broker = new Broker(_connection);
        broker.StartAsync(Orders, T0, Ct).GetAwaiter().GetResult();

        foreach (var text in new[] { "q1", "q2", "q3" })
        {
            Assert.True(broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(text), T0, "pricing", Ct)
                .GetAwaiter().GetResult().Succeeded);
        }
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<IReadOnlyList<Exchange>> OutstandingFor(AgentName agent, ResumePolicy policy, IResumeGate? gate = null) =>
        new Broker(_connection, BrokerOptions.Default with { ResumePolicy = policy, ResumeGate = gate ?? AlwaysResume.Instance })
            .OutstandingToWakeForAsync(agent, Ct);

    [Fact]
    public async Task Always_wakes_an_agent_for_everything_it_owes()
    {
        Assert.Equal(3, (await OutstandingFor(Billing, ResumePolicy.Always)).Count);
    }

    [Fact]
    public async Task Never_wakes_nobody_and_the_work_stays_open()
    {
        Assert.Empty(await OutstandingFor(Billing, ResumePolicy.Never));
        Assert.Equal(3, (await new ExchangeRepository(_connection).GetOpenAsync(cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task Ask_declines_rather_than_inventing_consent()
    {
        // Somebody must be asked, and the broker has no way to ask. The person confirms by starting the agent.
        Assert.Empty(await OutstandingFor(Billing, ResumePolicy.Ask));
    }

    [Fact]
    public async Task Only_what_the_named_agent_owes_is_counted()
    {
        Assert.Empty(await OutstandingFor(Orders, ResumePolicy.Always));
    }

    [Fact]
    public async Task The_gate_is_asked_once_per_exchange()
    {
        // A budget gate that has just permitted several may reasonably decline the next, which it cannot do if it
        // is consulted once for the batch.
        var gate = new CountingGate(permitFirst: 2);

        var outstanding = await OutstandingFor(Billing, ResumePolicy.Always, gate);

        Assert.Equal(3, gate.Asked);
        Assert.Equal(2, outstanding.Count);
    }

    [Fact]
    public async Task The_gate_is_not_consulted_when_the_policy_already_said_no()
    {
        var gate = new CountingGate(permitFirst: int.MaxValue);

        await OutstandingFor(Billing, ResumePolicy.Never, gate);

        Assert.Equal(0, gate.Asked);
    }

    [Fact]
    public async Task A_gate_that_throws_surfaces_rather_than_being_absorbed()
    {
        // A gate is the host's own code, so a throw is a defect it needs to see. A broker that swallowed it would
        // leave agents silently not being woken.
        await Assert.ThrowsAsync<InvalidOperationException>(() => OutstandingFor(Billing, ResumePolicy.Always, new ThrowingGate()));
    }

    private sealed class ThrowingGate : IResumeGate
    {
        public ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the budget service is unreachable");
    }

    private sealed class CountingGate(int permitFirst) : IResumeGate
    {
        public int Asked { get; private set; }

        public ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken = default)
        {
            Asked++;
            return ValueTask.FromResult(Asked <= permitFirst);
        }
    }
}
