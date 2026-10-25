using Google.Protobuf;
using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>Review probe, round 16: a purge against Requests and placements still arriving.</summary>
/// <summary>
/// That a purge leaves nothing for live traffic to revive: a Request racing it, or the purged agent still attached.
/// </summary>
/// <remarks>
/// Kept from review round 16, where both held. A purge records each conversation it destroys, so a Request into one is
/// refused afterwards as into a destroyed conversation, and the purge runs inside the broker's write gate.
/// </remarks>
public sealed class PurgeAgainstLiveTrafficTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_racing_the_purge_into_a_doomed_conversation_never_leaves_it_alive()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", Ct);

        var operators = broker.ConnectAsOperator();

        for (var round = 0; round < 15; round++)
        {
            await using var doomedAgent = await AttachedClient.AttachAsync(broker, $"infra{round}@alpha", $"C:/i{round}", Ct);

            var doomed = Assert.Single(
                (await docs.Client.RequestAsync(Requests.Open(docs.Name, doomedAgent.Name, $"who owns dns {round}"), cancellationToken: Ct))
                .Outcomes);

            var racing = Task.Run(
                async () => await docs.Client.RequestAsync(
                    Requests.Open(docs.Name, doomedAgent.Name, $"and the mx records {round}", doomed.ConversationId),
                    cancellationToken: Ct),
                Ct);

            var purged = await operators.PurgeAsync(new Wire.AgentRequest { Agent = doomedAgent.Name }, cancellationToken: Ct);
            var raced = await racing;

            Assert.Equal(Wire.PurgeVerdict.Gone, purged.Verdict);

            var listed = await operators.ListConversationsAsync(
                new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: Ct);

            Assert.DoesNotContain(listed.Conversations, c => c.ConversationId == doomed.ConversationId);

            // After the purge, the name stays refused.
            var after = Assert.Single(
                (await docs.Client.RequestAsync(
                    Requests.Open(docs.Name, "billing@alpha", "anything", doomed.ConversationId), cancellationToken: Ct))
                .Outcomes);

            Assert.False(after.Ok, $"round {round}: a request into the purged conversation was admitted");
            _ = raced;
        }
    }

    [Fact]
    public async Task An_agent_still_attached_when_purged_cannot_reopen_its_conversations_or_place_into_them()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", Ct);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", Ct);

        var operators = broker.ConnectAsOperator();

        var doomed = Assert.Single(
            (await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, "who owns dns"), cancellationToken: Ct))
            .Outcomes);

        var purged = await operators.PurgeAsync(new Wire.AgentRequest { Agent = infra.Name }, cancellationToken: Ct);
        Assert.Equal(Wire.PurgeVerdict.Gone, purged.Verdict);

        // infra's stream is still open. It names its own conversation again.
        string what;

        try
        {
            var reply = await infra.Client.RequestAsync(
                Requests.Open(infra.Name, docs.Name, "back again", doomed.ConversationId), cancellationToken: Ct);
            var outcome = Assert.Single(reply.Outcomes);
            what = $"request ok={outcome.Ok} rejection={outcome.Rejection}";
            Assert.False(outcome.Ok, what);
        }
        catch (RpcException refused)
        {
            what = $"request threw {refused.StatusCode}: {refused.Status.Detail}";
        }

        Wire.PlaceFileReply? placed = null;
        string placing;

        try
        {
            using var call = infra.Client.PlaceFile(cancellationToken: Ct);
            await call.RequestStream.WriteAsync(
                new Wire.PlaceFileFrame
                {
                    Header = new Wire.PlaceFileHeader { Agent = infra.Name, Name = "notes.md", ConversationId = doomed.ConversationId },
                },
                Ct);
            await call.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFromUtf8("hello") }, Ct);
            await call.RequestStream.CompleteAsync();
            placed = await call.ResponseAsync;
            placing = $"place outcome={placed.Outcome} rejection={placed.Rejection}";
        }
        catch (RpcException refused)
        {
            placing = $"place threw {refused.StatusCode}: {refused.Status.Detail}";
        }

        var listed = await operators.ListConversationsAsync(
            new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: Ct);
        var registry = await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.True(
            listed.Conversations.All(c => c.ConversationId != doomed.ConversationId)
            && (placed is null || placed.Rejection != Wire.FileRejection.None),
            $"{what}; {placing}; registry: {string.Join(",", registry.Rows.Select(a => a.Name))}");
    }
}
