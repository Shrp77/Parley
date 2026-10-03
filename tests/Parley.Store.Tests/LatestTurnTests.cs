using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the store can say how each agent's most recent turn ended (SPEC.md §11.6, specs/024).
/// </summary>
/// <remarks>
/// The read the operator registry makes on every refresh of a conversation, so a surface can tell an agent that stopped
/// with a refused permission from one still working. Most recent by when it was recorded, which is when it ended.
/// </remarks>
public sealed class LatestTurnTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@here");
    private static readonly AgentName Billing = AgentName.Create("billing@here");
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Each_agents_last_turn_is_read_with_when_it_ended_and_what_was_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        using var connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Headless, null, null, null, false, AgentStatus.Active), ct);
        }

        var turns = new InvocationRepository(connection);

        // Orders: a clean turn, then a later one that had two calls refused.
        await turns.RecordAsync(Orders, 1, 0, T0, TimeSpan.FromSeconds(30), 0, false, cancellationToken: ct);
        await turns.RecordAsync(Orders, 1, 1, T0.AddMinutes(5), TimeSpan.FromSeconds(90), 0, false, permissionDenials: 2, cancellationToken: ct);

        // Billing: one turn that was cut off for running too long.
        await turns.RecordAsync(Billing, 1, 1, T0, TimeSpan.FromMinutes(20), 0, true, cancellationToken: ct);

        var latest = (await turns.LatestTurnsAsync(ct)).ToDictionary(turn => turn.Agent);

        Assert.Equal(2, latest.Count);

        Assert.Equal(T0.AddMinutes(5).AddSeconds(90), latest[Orders].EndedUtc);
        Assert.True(latest[Orders].Clean);
        Assert.Equal(2, latest[Orders].PermissionDenials);

        Assert.False(latest[Billing].Clean);
        Assert.Equal(0, latest[Billing].PermissionDenials);
    }

    [Fact]
    public async Task A_turn_recorded_as_ending_past_the_calendar_is_still_read()
    {
        // The review gate's finding: such a row made this read throw, and the registry with it, for the whole broker. The
        // broker refuses such a report now; a store written before that must still be readable.
        var ct = TestContext.Current.CancellationToken;

        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Headless, null, null, null, false, AgentStatus.Active), ct);

        var turns = new InvocationRepository(connection);
        await turns.RecordAsync(
            Orders, 1, 1, new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(2), 0, false,
            cancellationToken: ct);

        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await turns.LatestTurnsAsync(ct)).EndedUtc);
    }

    [Fact]
    public async Task An_agent_with_no_recorded_turn_has_no_entry()
    {
        var ct = TestContext.Current.CancellationToken;

        using var connection = ParleyDatabase.OpenInMemory();

        Assert.Empty(await new InvocationRepository(connection).LatestTurnsAsync(ct));
    }
}
