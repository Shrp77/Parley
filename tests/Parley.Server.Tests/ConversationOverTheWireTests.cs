using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A whole exchange carried over a real socket, per SPEC.md §4 and §4.0.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the architecture exists for. Everything else in the repository can pass while the broker is a
/// file two processes both open — which §4 calls corrupt rather than slow, and which no test that constructs a
/// broker directly can tell apart from a working one.
/// </para>
/// <para>
/// So: two clients, two channels, one listener, and a Request that goes from one to the other and comes back with
/// its Result. Nothing here touches a <c>Broker</c> or a <c>SqliteConnection</c>.
/// </para>
/// </remarks>
public sealed class ConversationOverTheWireTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_request_reaches_a_participant_and_its_result_comes_back()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);

        var sent = Assert.Single((await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, "who owns the retry policy"), cancellationToken: token)).Outcomes);

        Assert.True(sent.Ok);
        Assert.Equal(infra.Name, sent.Receiver);
        Assert.StartsWith("c-", sent.ConversationId, StringComparison.Ordinal);

        // The broker spoke first: nothing polled, and the notice arrived because a socket was already open. And it
        // carried no part of what was said.
        var notice = await infra.WaitForNoticeAsync(Patience);
        Assert.Equal(1, notice.MessagesWaiting);
        Assert.DoesNotContain("retry", notice.ToString(), StringComparison.OrdinalIgnoreCase);

        var delivered = Assert.Single((await infra.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token)).Messages);
        Assert.Equal(docs.Name, delivered.Message.From);
        Assert.Equal(Wire.MessageType.Request, delivered.Message.Type);
        Assert.Contains("retry policy", delivered.Message.PayloadJson, StringComparison.Ordinal);
        Assert.NotEmpty(delivered.Envelope);

        var result = await infra.Client.ResultAsync(Requests.Result(infra.Name, sent.ExchangeId), cancellationToken: token);
        Assert.True(result.Ok);

        Assert.Equal(1, (await docs.WaitForNoticeAsync(Patience)).MessagesWaiting);

        var back = Assert.Single((await docs.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: token)).Messages);
        Assert.Equal(Wire.MessageType.Result, back.Message.Type);
        Assert.Contains("billing owns it", back.Message.PayloadJson, StringComparison.Ordinal);

        // The requester's own words come back beside the Result, because its context has moved on (§13).
        Assert.Contains("who owns the retry policy", back.Envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_is_reachable_because_its_stream_is_open_and_not_otherwise()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        Assert.Contains("docs@alpha", broker.Runtime.Attached);

        // §4.0: the stream ending is the agent going away, however it ends.
        await docs.DisposeAsync();

        await broker.WaitUntilDetachedAsync("docs@alpha", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_request_to_an_agent_that_is_not_attached_is_still_opened()
    {
        // A name outlives a connection, and a machine that is switched off is still asked, so its silence is flagged
        // rather than silently absent.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        var infra = await AttachedClient.AttachAsync(broker, "infra@beta", "C:/repo-infra", token);
        await infra.DisposeAsync();

        var sent = Assert.Single((await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra@beta", "who owns the domain controller"), cancellationToken: token)).Outcomes);

        Assert.True(sent.Ok);
        Assert.Equal("infra@beta", sent.Receiver);
    }

    [Fact]
    public async Task An_operation_before_an_attach_is_refused()
    {
        // Not an authorisation check: §16 says the wire cannot tell who is calling. It says only that no stream is
        // open under that name, and there is therefore no broker to run the operation against.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(
            async () => await broker.Connect().ListAgentsAsync(new Wire.ListAgentsRequest { AskingAs = "billing@alpha" }, cancellationToken: token));

        Assert.Equal(Grpc.Core.StatusCode.FailedPrecondition, refused.StatusCode);
    }

    [Fact]
    public async Task A_second_repository_cannot_take_a_name_that_is_held()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var first = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-one", token);

        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(
            async () => await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-two", token));

        Assert.Equal(Grpc.Core.StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("C:/repo-one", refused.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_request_opens_one_exchange_per_machine_in_one_conversation_and_wakes_each()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await using var alpha = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);
        await using var beta = await AttachedClient.AttachAsync(broker, "infra@beta", "C:/repo-infra", token);

        var sent = await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra", "what does this machine believe about DNS"), cancellationToken: token);

        Assert.Equal(2, sent.Outcomes.Count);
        Assert.All(sent.Outcomes, o => Assert.True(o.Ok));
        Assert.Single(sent.Outcomes.Select(o => o.ConversationId).Distinct());
        Assert.Single(sent.Outcomes.Select(o => o.RequestGroup).Distinct());

        Assert.Equal(1, (await alpha.WaitForNoticeAsync(Patience)).MessagesWaiting);
        Assert.Equal(1, (await beta.WaitForNoticeAsync(Patience)).MessagesWaiting);

        Assert.Equal(["infra@alpha", "infra@beta"], sent.Outcomes.Select(o => o.Receiver).Order(StringComparer.Ordinal));
    }
}
