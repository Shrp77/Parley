using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That everyone sees the person as here while a surface holds their stream and away once it is dropped — never stranded,
/// never startable — and that being away changes nothing about asking them (SPEC.md §14).
/// </summary>
/// <remarks>
/// Read back through both services, the operator's registry and the roster an agent is shown, because the defect this
/// closes was on both: the person read <c>stranded</c> in <c>parley registry</c> and in <c>parley_agents</c>, telling a
/// peer an absent person could not be reached.
/// </remarks>
[Integration]
public sealed class PersonIsHereOrAwayTests
{
    private const string Person = "human-user@test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Wire.AgentPresence Registry, Wire.AgentPresence Roster)> ReadAsync(
        LoopbackBroker broker, AttachedClient asking)
    {
        var registry = (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct))
            .Rows.Single(row => row.Name == Person).Presence;

        var roster = (await asking.Client.ListAgentsAsync(
                new Wire.ListAgentsRequest { AskingAs = asking.Name }, cancellationToken: Ct))
            .Agents.Single(agent => agent.Name == Person).Presence;

        return (registry, roster);
    }

    [Fact]
    public async Task The_person_is_here_while_a_surface_holds_them_and_away_once_it_lets_go()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@m1", cancellationToken: Ct);

        Assert.Equal((Wire.AgentPresence.Away, Wire.AgentPresence.Away), await ReadAsync(broker, orders));

        var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);

        Assert.Equal((Wire.AgentPresence.Here, Wire.AgentPresence.Here), await ReadAsync(broker, orders));

        await seat.DisposeAsync();

        // Detaching is observed by the broker a moment later, as every agent's is.
        var away = (Wire.AgentPresence.Here, Wire.AgentPresence.Here);

        for (var tries = 0; tries < 50 && away != (Wire.AgentPresence.Away, Wire.AgentPresence.Away); tries++)
        {
            await Task.Delay(100, Ct);
            away = await ReadAsync(broker, orders);
        }

        Assert.Equal((Wire.AgentPresence.Away, Wire.AgentPresence.Away), away);
    }
}
