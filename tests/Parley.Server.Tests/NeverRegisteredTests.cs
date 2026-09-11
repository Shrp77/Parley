using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What can and cannot reach a catalogued role that has never registered.
/// </summary>
/// <remarks>
/// <para>
/// This is the state a repository is in the moment it is enrolled: the catalogue names it, so a
/// node can start it, and nothing has ever attached under the name. On a fresh deployment it is the
/// state <em>most</em> agents are in, and the roster shows them as a bare role rather than
/// <c>role@machine</c> because there is no registration to spell.
/// </para>
/// <para>
/// Written because a message on the enrolment screen makes a claim about it, and a claim on screen
/// is worth a test.
/// </para>
/// </remarks>
public sealed class NeverRegisteredTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task An_operator_can_start_one()
    {
        // §12.3, and already covered by StartOnDemandTests. Repeated here as the control: this is
        // the one path that does reach it, so the two below are about a difference rather than
        // about the agent being unreachable in general.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var asked = await broker.ConnectAsOperator().StartAsync(
            new Wire.StartRequest { Agent = "orders@alpha", Because = "just enrolled" },
            cancellationToken: token);

        Assert.True(asked.Changed);

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
    }

    [Fact]
    public async Task A_question_to_one_is_refused_and_starts_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var reply = await sender.Client.AskAsync(
            new Wire.AskRequest
            {
                From = "billing@alpha",
                To = "orders@alpha",
                ResponderModel = "test",
                PayloadJson = """{"intent":"waking it","q1":"are you there?","consequences":"if it answers I will ask the real question, and if not I will start it by hand instead."}""",
                ConversationId = "c1"
            },
            cancellationToken: token);

        Assert.False(reply.Ok);
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task A_notification_to_one_is_refused_and_starts_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var reply = await sender.Client.NotifyAsync(
            new Wire.NotifyRequest
            {
                From = "billing@alpha",
                To = "orders@alpha",
                ResponderModel = "test",
                PayloadJson = """{"text":"the schema moved"}""",
                ConversationId = "c1"
            },
            cancellationToken: token);

        Assert.False(reply.Ok);
        Assert.Equal(Wire.NotifyRejection.RecipientUnroutable, reply.Refusal);
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }
}
