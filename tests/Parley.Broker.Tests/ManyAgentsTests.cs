using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The deployment §1 describes: many repositories, an agent apiece, sessions opening and closing
/// independently.
/// </summary>
/// <remarks>
/// This is the case that forced the attach sequence to be scoped. Under the broker-wide version
/// the second session to open orphaned the first's live work, the third orphaned both, and
/// nothing said so — the damage scaled with how many colleagues were working and was invisible
/// to all of them. A machine rebooting and everyone coming back is exactly when it would have
/// been worst, and exactly when nobody would have suspected the messaging layer.
/// </remarks>
public sealed class ManyAgentsTests : IDisposable
{
    private const int Repositories = 22;
    private const string Model = "claude-opus-5";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly AgentName[] Agents =
        [.. Enumerable.Range(1, Repositories).Select(i => AgentName.Create($"repo-{i}"))];

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;

    public ManyAgentsTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        var registry = new AgentRepository(_connection);

        foreach (var agent in Agents)
        {
            registry.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Full, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static string Question(int i) =>
        $$"""{"text":"who owns field {{i}}?","consequences":"If you do I read it; if not I define my own."}""";

    private Broker Attach(AgentName agent, DateTimeOffset at)
    {
        var broker = new Broker(_connection, BrokerOptions.Default);
        broker.StartAsync(agent, at, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return broker;
    }

    [Fact]
    public async Task Sessions_opening_one_by_one_leave_each_others_work_alone()
    {
        var ct = TestContext.Current.CancellationToken;

        // Every repository asks the next one something. A ring, so each agent owes exactly one
        // answer and is waiting on exactly one — and no pair carries two questions, so nothing
        // trips the cycle check.
        var opened = new List<long>();
        var host = Attach(Agents[0], T0);

        for (var i = 0; i < Repositories - 1; i++)
        {
            var asked = await host.AskAsync(
                Agents[i], Agents[i + 1], Model, Question(i), T0.AddMinutes(1), cancellationToken: ct);
            Assert.True(asked.Succeeded, $"expected admission for {i}, got {asked.Rejection}");
            opened.Add(asked.Exchange!.Id);
        }

        // Now everybody's session opens, one after another, as people come back to work.
        for (var i = 0; i < Repositories; i++)
        {
            Attach(Agents[i], T0.AddMinutes(2 + i));
        }

        // Each agent released exactly the one answer it owed, and nothing else. Under the
        // broker-wide sequence the first attach would have orphaned all twenty-one at once.
        var states = new List<ExchangeState>();

        foreach (var id in opened)
        {
            states.Add((await _exchanges.GetAsync(id, cancellationToken: ct))!.State);
        }

        Assert.All(states, state => Assert.Equal(ExchangeState.Orphaned, state));
    }

    [Fact]
    public async Task One_session_opening_orphans_one_debt_and_no_more()
    {
        var ct = TestContext.Current.CancellationToken;

        var host = Attach(Agents[0], T0);
        var opened = new List<long>();

        for (var i = 0; i < Repositories - 1; i++)
        {
            var asked = await host.AskAsync(
                Agents[i], Agents[i + 1], Model, Question(i), T0.AddMinutes(1), cancellationToken: ct);
            opened.Add(asked.Exchange!.Id);
        }

        // One person comes back and opens one repository.
        var broker = new Broker(_connection, BrokerOptions.Default);
        var attach = await broker.StartAsync(Agents[5], T0.AddMinutes(2), ct);

        Assert.Equal(1, attach.ExchangesOrphaned);

        var orphaned = 0;

        foreach (var id in opened)
        {
            if ((await _exchanges.GetAsync(id, cancellationToken: ct))!.State == ExchangeState.Orphaned)
            {
                orphaned++;
            }
        }

        // Exactly the one debt repo-5 was carrying. Twenty other conversations untouched.
        Assert.Equal(1, orphaned);
    }
}
