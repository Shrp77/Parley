using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That retiring an agent stops it being routed to and listed, including while it is still attached.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §10.2 defines the operation exactly: <c>retire(agent)</c> marks an
/// agent retired "so it is no longer routed to or listed". Two claims, both checkable, and neither
/// covered over the wire — <c>NeverRegisteredTests</c> probes an agent that never registered, which §14
/// treats as a different state with a different remedy.
/// </para>
/// <para>
/// <b>The case worth probing is retirement while attached.</b> The broker enforces retirement at attach:
/// <c>MarkLiveAsync</c> declines a retired agent, so one cannot come back. That is the path anybody would
/// think of. An agent that was already attached when the operator retired it has passed that check
/// already, and whether the *routing* rule holds for it is a property of the send rather than of the
/// connection — which is where this repository has found the same class of gap before, in the seat that
/// went on speaking after the book had released it.
/// </para>
/// <para>
/// <b>What is deliberately not asserted:</b> that a retired agent cannot *send*. §10.2 says it is not
/// routed to and not listed, and says nothing about silencing it — and §14's "refused every operation"
/// is about an agent that has **never registered**, which is a different state. An agent retired while
/// holding work that it then answers is not obviously wrong, and asserting otherwise would encode a
/// reading the document does not carry.
/// </para>
/// </remarks>
[Integration]
public sealed class RetirementHoldsTests
{
    [Fact]
    public async Task A_retired_agent_is_neither_routed_to_nor_listed_even_while_it_is_still_attached()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        var operators = broker.ConnectAsOperator();

        // It works before, so a refusal afterwards is retirement and not the fixture.
        var before = Assert.Single(
            (await docs.Client.RequestAsync(
                Requests.Open(docs.Name, infra.Name, "who owns dns"),
                cancellationToken: token)).Outcomes);

        Assert.True(before.Ok, "the request was refused before any retirement, so this probe measured nothing");

        Assert.Contains(
            (await docs.Client.ListAgentsAsync(
                new Wire.ListAgentsRequest { AskingAs = docs.Name }, cancellationToken: token)).Agents,
            agent => agent.Name == infra.Name);

        // Retired, and deliberately left attached — it is holding its connection throughout.
        var retired = await operators.RetireAsync(
            new Wire.AgentRequest { Agent = infra.Name }, cancellationToken: token);

        Assert.True(retired.Changed, "the retirement changed nothing, so the agent was not retired");

        // **No longer routed to.**
        var after = Assert.Single(
            (await docs.Client.RequestAsync(
                Requests.Open(docs.Name, infra.Name, "and who owns dhcp"),
                cancellationToken: token)).Outcomes);

        Assert.False(
            after.Ok,
            "a Request reached an agent the operator had retired, so retirement does not hold for a "
            + "connection that was already open when it was made");

        // **No longer listed.**
        var listed = await docs.Client.ListAgentsAsync(
            new Wire.ListAgentsRequest { AskingAs = docs.Name }, cancellationToken: token);

        Assert.DoesNotContain(listed.Agents, agent => agent.Name == infra.Name);

        // And it comes back, so the refusal is retirement rather than anything the fixture broke.
        Assert.True(
            (await operators.ReinstateAsync(
                new Wire.AgentRequest { Agent = infra.Name }, cancellationToken: token)).Changed,
            "the reinstatement changed nothing");

        Assert.True(
            Assert.Single(
                (await docs.Client.RequestAsync(
                    Requests.Open(docs.Name, infra.Name, "and now"),
                    cancellationToken: token)).Outcomes).Ok,
            "a reinstated agent could still not be reached, so the refusal above was not retirement");
    }
}
