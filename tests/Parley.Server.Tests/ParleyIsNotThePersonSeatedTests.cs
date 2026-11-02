using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the facilitator is never the person an agent escalates to (SPEC.md principle 2.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the guarantee actually lives.</b> An agent that no peer can help asks the person, and the only way it
/// learns who that is, is its roster: the person is listed with the adapter <see cref="PersonSeat.Adapter"/>. So the
/// facilitator is not the person for exactly as long as it is off that roster and its adapter is not the seat's. Both
/// halves are asserted, so the one that broke is named.
/// </para>
/// <para>
/// <b>This test once asserted the opposite, and the change is the point.</b> It was called
/// <c>With_nobody_seated_an_agent_finds_no_person_to_ask</c>: with nobody seated the need went back to the originator,
/// and a facilitator counted as a person would have quietly taken that away. Now the broker provides the person
/// seat in every project, so an agent always finds exactly one person — and the half worth keeping is that it is never
/// Parley.
/// </para>
/// </remarks>
[Integration]
public sealed class ParleyIsNotThePersonSeatedTests
{
    [Fact]
    public async Task The_one_person_an_agent_finds_is_the_person_seat_and_never_Parley()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        var roster = await orders.Client.ListAgentsAsync(
            new Wire.ListAgentsRequest { AskingAs = "orders@alpha" }, cancellationToken: token);

        var person = Assert.Single(roster.Agents, a => a.Adapter == PersonSeat.Adapter);

        Assert.True(PersonSeat.Is(AgentName.Create(person.Name)), $"the person listed was '{person.Name}'.");
        Assert.DoesNotContain(roster.Agents, a => a.Adapter == Facilitation.Adapter);
        Assert.NotEqual(PersonSeat.Adapter, Facilitation.Adapter);
    }
}
