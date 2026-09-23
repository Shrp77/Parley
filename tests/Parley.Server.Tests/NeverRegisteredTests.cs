using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What can and cannot reach a catalogued role that has never registered.
/// </summary>
/// <remarks>
/// This is the state a repository is in the moment it is enrolled: the catalogue names it, so a node can start it,
/// and nothing has ever attached under the name. On a fresh deployment it is the state most agents are in.
/// </remarks>
public sealed class NeverRegisteredTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task An_operator_can_start_one()
    {
        // §12.3. The control: this is the one path that does reach it, so the test below is about a difference
        // rather than about the agent being unreachable in general.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var started = await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = "just enrolled" }, cancellationToken: token);

        Assert.True(started.Changed);
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, (await node.WaitForCommandAsync(Patience)).CommandCase);
    }

    [Fact]
    public async Task A_request_to_one_is_refused_and_starts_nothing()
    {
        // §14 refuses an unregistered agent every operation, and the refusal answers before any wake is
        // considered, so a sender cannot use the wake to probe for names.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var outcome = Assert.Single((await sender.Client.RequestAsync(Requests.Open("billing@alpha", "orders@alpha", "are you there?", "c1"), cancellationToken: token)).Outcomes);

        Assert.False(outcome.Ok);
        Assert.Equal(Wire.RequestRejection.ReceiverNotRegistered, outcome.Rejection);
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }
}
