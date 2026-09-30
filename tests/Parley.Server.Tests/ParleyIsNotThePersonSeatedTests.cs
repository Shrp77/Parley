using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the facilitator is never the person an agent escalates to (SPEC.md principle 2.6, FR-004).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the guarantee actually lives.</b> An agent that no peer can help asks "the person seated in the deployment",
/// and the only way it learns who that is, is its roster: a seat is listed with the adapter
/// <see cref="Seats.Adapter"/>. So the facilitator is not the person seated for exactly as long as it is off that
/// roster and its adapter is not a seat's. Both halves are asserted, so the one that broke is named.
/// </para>
/// <para>
/// With nobody seated the roster must offer no person at all — the need goes back to the originator, which is the
/// half a facilitator that counted as a person would quietly take away.
/// </para>
/// </remarks>
public sealed class ParleyIsNotThePersonSeatedTests
{
    [Fact]
    public async Task With_nobody_seated_an_agent_finds_no_person_to_ask()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        var roster = await orders.Client.ListAgentsAsync(
            new Wire.ListAgentsRequest { AskingAs = "orders@alpha" }, cancellationToken: token);

        Assert.DoesNotContain(roster.Agents, a => a.Adapter == Seats.Adapter);
        Assert.DoesNotContain(roster.Agents, a => a.Adapter == Facilitation.Adapter);
        Assert.NotEqual(Seats.Adapter, Facilitation.Adapter);
    }
}
