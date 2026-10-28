using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The deployment §1 describes: many repositories, an agent apiece, sessions opening and closing independently.
/// </summary>
/// <remarks>
/// This is the case that forced the attach sequence to be scoped to the attaching agent. A sequence reaching
/// every agent's work would have one session's arrival act on colleagues' work, with the damage scaling with how
/// many were working and invisible to all of them.
/// </remarks>
public sealed class ManyAgentsTests : IDisposable
{
    private const int Repositories = 22;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly AgentName[] Agents =
        [.. Enumerable.Range(1, Repositories).Select(i => AgentName.Create($"repo-{i}@test"))];

    private readonly SqliteConnection _connection;
    private readonly MessageRepository _messages;

    public ManyAgentsTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Agents);
        _messages = new MessageRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A ring of Requests, each collected and none finished, as a day of work would leave it.</summary>
    private async Task<List<long>> RingCollectedAsync()
    {
        var host = new Broker(_connection);
        await host.StartAsync(Agents[0], T0, Ct);

        var opened = new List<long>();

        for (var i = 0; i < Repositories - 1; i++)
        {
            // Naming nothing: each agent past the first holds the Request before it and asks onward for part of
            // it, so the whole ring is one line of work and the broker keeps it in one conversation (§5.0).
            var sent = await host.RequestAsync(
                Agents[i], Agents[i + 1], Requests.Model, Requests.Request($"who owns field {i}?"), T0.AddMinutes(1), cancellationToken: Ct);
            Assert.True(sent.Succeeded, $"expected admission for {i}, got {sent.Rejection}");
            opened.Add(sent.Exchange!.Id);
        }

        foreach (var agent in Agents)
        {
            await host.DrainInboxAsync(agent, T0.AddMinutes(90), cancellationToken: Ct);
        }

        await host.DetachAsync(Ct);

        return opened;
    }

    [Fact]
    public async Task Sessions_opening_one_by_one_each_get_back_exactly_their_own_work()
    {
        await RingCollectedAsync();

        var redelivered = new List<int>();

        for (var i = 0; i < Repositories; i++)
        {
            redelivered.Add((await new Broker(_connection).StartAsync(Agents[i], T0.AddHours(2 + i), Ct)).ExchangesRedelivered);
        }

        // The first repository owes nothing in the ring; every other owes exactly one.
        Assert.Equal([0, .. Enumerable.Repeat(1, Repositories - 1)], redelivered);
    }

    [Fact]
    public async Task One_session_opening_hands_back_one_piece_of_work_and_no_more()
    {
        await RingCollectedAsync();

        var attach = await new Broker(_connection).StartAsync(Agents[5], T0.AddHours(2), Ct);

        Assert.Equal(1, attach.ExchangesRedelivered);

        var waiting = 0;

        foreach (var agent in Agents)
        {
            waiting += await _messages.CountPendingAsync(agent, cancellationToken: Ct);
        }

        // Exactly the one piece of work repo-6 was carrying. Twenty other lines of work untouched.
        Assert.Equal(1, waiting);
        Assert.Equal(1, await _messages.CountPendingAsync(Agents[5], cancellationToken: Ct));
    }
}
