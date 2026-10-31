using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the agent which sent a Request to a role is told how many it reached and how many have answered (SPEC.md §5.5).
/// </summary>
/// <remarks>
/// Each Result it collects carries both numbers as they stand at that delivery, and a person reading the conversation
/// can count the same from the exchanges: every member carries the group. Over a real socket, because a number nobody
/// can read through the protocol is not evidence the protocol reports it.
/// </remarks>
[Integration]
public sealed class RoleRequestProgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_result_says_how_many_were_asked_and_how_many_have_answered()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", Ct);
        await using var alpha = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", Ct);
        await using var beta = await AttachedClient.AttachAsync(broker, "infra@beta", "C:/repo-infra", Ct);
        await using var gamma = await AttachedClient.AttachAsync(broker, "infra@gamma", "C:/repo-infra", Ct);

        var sent = await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra", "what does this machine believe about DNS"), cancellationToken: Ct);
        Assert.Equal(3, sent.Outcomes.Count);
        var exchange = sent.Outcomes.ToDictionary(o => o.Receiver, o => o.ExchangeId);

        Assert.True((await alpha.Client.ResultAsync(Requests.Result(alpha.Name, exchange[alpha.Name]), cancellationToken: Ct)).Ok);

        var first = Assert.Single(
            (await docs.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: Ct)).Messages);
        Assert.Equal(3, first.RoleRequest.Asked);
        Assert.Equal(1, first.RoleRequest.Answered);

        Assert.True((await beta.Client.ResultAsync(Requests.Result(beta.Name, exchange[beta.Name]), cancellationToken: Ct)).Ok);

        var second = Assert.Single(
            (await docs.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: Ct)).Messages);
        Assert.Equal(3, second.RoleRequest.Asked);
        Assert.Equal(2, second.RoleRequest.Answered);

        // What a person's surface reads: every member carries the one group.
        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = sent.Outcomes[0].ConversationId }, cancellationToken: Ct);
        Assert.Equal(3, export.Exchanges.Count(e => e.RequestGroup == sent.Outcomes[0].RequestGroup && e.RequestGroup.Length > 0));
    }

    [Fact]
    public async Task The_agents_asked_are_never_told_how_many_others_were()
    {
        // SPEC.md §5.5, "The receivers do not know": when the person closes the conversation, each member collects the
        // broker's closed Result too, and it must carry no count (lens 3 run 86 found this held by nothing).
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", Ct);
        await using var alpha = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", Ct);
        await using var beta = await AttachedClient.AttachAsync(broker, "infra@beta", "C:/repo-infra", Ct);

        var sent = await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra", "what does this machine believe about DNS"), cancellationToken: Ct);
        var conversation = sent.Outcomes[0].ConversationId;

        // What each member was asked carries nothing of the group either.
        foreach (var member in (AttachedClient[])[alpha, beta])
        {
            var asked = Assert.Single((await member.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = member.Name }, cancellationToken: Ct)).Messages);
            Assert.Null(asked.RoleRequest);
        }

        Assert.True((await alpha.Client.ResultAsync(
            Requests.Result(alpha.Name, sent.Outcomes.Single(o => o.Receiver == alpha.Name).ExchangeId), cancellationToken: Ct)).Ok);

        Assert.True((await broker.ConnectAsOperator().ForceConcludeAsync(
            new Wire.ForceConcludeRequest { ConversationId = conversation, Reason = "settled on a call" }, cancellationToken: Ct)).Changed);

        // beta had not answered, so the close reaches it as a closed Result; it says nothing of how many were asked.
        var closed = Assert.Single((await beta.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = beta.Name }, cancellationToken: Ct)).Messages);
        Assert.Equal(Wire.MessageType.Result, closed.Message.Type);
        Assert.Null(closed.RoleRequest);

        var requester = (await docs.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: Ct)).Messages;
        Assert.Contains(requester, m => m.RoleRequest is { Asked: 2 });
    }

    [Fact]
    public async Task A_result_to_a_request_for_one_agent_carries_no_count()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", Ct);
        await using var alpha = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", Ct);

        var sent = Assert.Single((await docs.Client.RequestAsync(
            Requests.Open(docs.Name, alpha.Name, "what does this machine believe about DNS"), cancellationToken: Ct)).Outcomes);
        Assert.True((await alpha.Client.ResultAsync(Requests.Result(alpha.Name, sent.ExchangeId), cancellationToken: Ct)).Ok);

        var result = Assert.Single(
            (await docs.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: Ct)).Messages);
        Assert.Null(result.RoleRequest);
    }
}
