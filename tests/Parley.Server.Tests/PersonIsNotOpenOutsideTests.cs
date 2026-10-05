using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That only an agent a node could start reads as open outside Parley (SPEC.md §11.7): never the person, never Parley.
/// </summary>
/// <remarks>
/// <b>Found while investigating the owner's report of 2026-10-05</b>: the live registry said the person was open outside
/// Parley, because their attachment is a surface and no surface is ever "started by Parley". Nothing drew it yet; a
/// surface reading the flag for every row would have marked the person with the agents' warning. Fails against 0.40.1.
/// </remarks>
public sealed class PersonIsNotOpenOutsideTests
{
    private static readonly string Person = PersonSeat.On(ProjectName.Create("test")).Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_seated_person_is_not_open_outside_and_an_agent_opened_by_hand_still_is()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: PersonSeat.Adapter);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var registry = await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.False(registry.Rows.Single(r => r.Name == Person).OpenOutside, "the person read as open outside Parley");
        Assert.All(
            registry.Rows.Where(r => Facilitation.Is(AgentName.Create(r.Name))),
            row => Assert.False(row.OpenOutside, "Parley read as open outside Parley"));

        // The flag still means what it means for an agent nobody started through a node.
        Assert.True(registry.Rows.Single(r => r.Name == orders.Name).OpenOutside);
    }
}
