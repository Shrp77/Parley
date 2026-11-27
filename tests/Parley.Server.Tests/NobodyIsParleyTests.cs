using System.Text.Json.Nodes;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That no caller can act as the facilitator, and no work can be sent to it (SPEC.md principle 2.11, §10.1, §12.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Over a real socket, because the doors are on the wire.</b> Storage refuses the reserved role too
/// (<c>FacilitatorRowTests</c>), but a refusal that surfaced as a storage exception would reach an adapter as an
/// internal error with no sentence in it. These assert the refusal a caller actually gets.
/// </para>
/// <para>
/// <b>The broker has been forged as a sender before</b> — an adapter claiming <c>parley:broker</c> was a review
/// finding. The facilitator is the one sender every agent is asked by without having asked for anything, so each door
/// here was watched to admit the forgery with its guard removed.
/// </para>
/// </remarks>
[Integration]
public sealed class NobodyIsParleyTests
{
    private static Wire.OpenRequest Request(string from, string to, string responder = "test") =>
        new()
        {
            From = from,
            To = to,
            ResponderModel = responder,
            PayloadJson = new JsonObject { ["ask"] = "what do you own?" }.ToJsonString(),
            ConversationId = "c1"
        };

    [Fact]
    public async Task An_agent_cannot_attach_under_the_facilitators_role()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var refused = await Assert.ThrowsAsync<RpcException>(
            () => AttachedClient.AttachAsync(broker, "parley-facilitator@elsewhere", cancellationToken: token));

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Contains("Parley", refused.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_cannot_attach_with_the_facilitators_adapter()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var stream = broker.Connect().Attach(
            new Wire.AttachRequest
            {
                Agent = "orders@alpha",
                Adapter = Facilitation.Adapter,
                Tier = Wire.AgentTier.Pull,
                ProtocolVersion = ProtocolVersion.Current
            },
            cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(() => stream.ResponseStream.MoveNext(token));

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
    }

    [Fact]
    public async Task Nobody_can_send_as_the_facilitator_even_attached_as_something_else()
    {
        // §16 concedes a caller can claim any name. The facilitator's is the one that claim must not reach.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await orders.Client.RequestAsync(
            Request(broker.Runtime.Facilitator.Value, "orders@alpha"), cancellationToken: token));

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
    }

    [Theory]
    [InlineData(Facilitation.Attribution)]
    [InlineData(Facilitation.PersonAttribution)]
    public async Task No_adapter_may_claim_either_of_the_facilitators_attributions(string responder)
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await billing.Client.RequestAsync(
            Request("billing@alpha", "orders@alpha", responder), cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
    }

    [Fact]
    public async Task Work_sent_to_the_facilitator_is_refused_with_a_reason()
    {
        // Leaving it off the roster does not stop an agent naming it, and a Request it will never answer would sit
        // flagged for ever.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var outcome = Assert.Single((await billing.Client.RequestAsync(
            Request("billing@alpha", broker.Runtime.Facilitator.Value), cancellationToken: token)).Outcomes);

        Assert.False(outcome.Ok);
        Assert.Equal(Wire.RequestRejection.ReceiverIsTheFacilitator, outcome.Rejection);
    }

    [Fact]
    public async Task No_agent_finds_the_facilitator_on_its_roster()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        var roster = await orders.Client.ListAgentsAsync(
            new Wire.ListAgentsRequest { AskingAs = "orders@alpha" }, cancellationToken: token);

        Assert.DoesNotContain(roster.Agents, a => a.Name == broker.Runtime.Facilitator.Value);

        // And the person does: the registry lists it, marked by its adapter so a surface shows it as Parley.
        var registry = await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token);

        Assert.Equal(
            Facilitation.Adapter,
            Assert.Single(registry.Rows, r => r.Name == broker.Runtime.Facilitator.Value).Adapter);
    }
}
