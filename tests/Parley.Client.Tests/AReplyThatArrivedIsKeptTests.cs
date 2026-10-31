using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// That a collection whose reply arrived whole is kept when the call breaks after it (SPEC.md §6).
/// </summary>
/// <remarks>
/// The broker may already have recorded it delivered, so dropping the reply could lose it for good; keeping it can at
/// worst show it twice, when the broker had not recorded it and hands it over again. Delivery is at least once.
/// </remarks>
[Integration]
public sealed class AReplyThatArrivedIsKeptTests
{
    [Fact]
    public async Task A_reply_is_returned_when_the_call_breaks_after_it_arrived()
    {
        var agents = new Breaking();

        var reply = await agents.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "human-user@alpha" }, TestContext.Current.CancellationToken);

        Assert.Equal(7, Assert.Single(reply.Messages).Message.ExchangeId);
    }

    private sealed class Breaking : Wire.Agents.AgentsClient
    {
        public override AsyncDuplexStreamingCall<Wire.DrainInboxFrame, Wire.InboxReply> DrainInbox(CallOptions options) =>
            FakeCollection.Answering(
                _ =>
                {
                    var reply = new Wire.InboxReply();
                    reply.Messages.Add(new Wire.DeliveredMessage { Message = new Wire.Message { Id = 1, ExchangeId = 7 } });
                    return reply;
                },
                breakAfterTheReply: true);
    }
}
