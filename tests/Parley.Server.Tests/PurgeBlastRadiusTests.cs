using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That purging an agent destroys exactly the conversations it was party to, and no others.
/// </summary>
/// <remarks>
/// <para>
/// Purge is the most destructive operation the protocol has, and SPEC.md
/// §10.2 states its reach precisely: it deletes "an agent and every conversation it was party to, with
/// those conversations' files… Destroys its peers' side of those conversations too, and cannot be
/// undone." So the property worth probing is **blast radius** — that it takes all of what it names and
/// none of what it does not.
/// </para>
/// <para>
/// <b>Both directions are failures, and they are different kinds.</b> Taking too little leaves a
/// conversation half-deleted, with the peer's side of an exchange referring to somebody the registry no
/// longer has — the schema's own comments record choosing text over keys precisely so that a purge
/// "deletes these rows by name and reports them, where a key would cascade silently or refuse the
/// purge", which is a decision to let application code get this right. Taking too much destroys a
/// conversation the person never asked to lose, and it cannot be undone.
/// </para>
/// <para>
/// <c>PurgedAgentTests</c> covers a Request aimed at a purged agent. Nothing covered what happened to the
/// conversations around it.
/// </para>
/// </remarks>
[Integration]
public sealed class PurgeBlastRadiusTests
{
    [Fact]
    public async Task Purging_an_agent_takes_its_conversations_and_leaves_every_other_one_whole()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/c", token);

        var operators = broker.ConnectAsOperator();

        // One conversation that the purge must take, answered so it holds traffic in both directions.
        var doomed = Assert.Single(
            (await docs.Client.RequestAsync(
                Requests.Open(docs.Name, infra.Name, "who owns dns"),
                cancellationToken: token)).Outcomes);

        _ = await infra.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token);

        Assert.True(
            (await infra.Client.ResultAsync(
                Requests.Result(infra.Name, doomed.ExchangeId), cancellationToken: token)).Ok);

        // One the purge must not touch, between two agents that have nothing to do with it.
        var spared = Assert.Single(
            (await docs.Client.RequestAsync(
                Requests.Open(docs.Name, billing.Name, "what is owed"),
                cancellationToken: token)).Outcomes);

        var before = await ConversationsAsync(operators, token);

        Assert.Contains(doomed.ConversationId, before);
        Assert.Contains(spared.ConversationId, before);

        var purged = await operators.PurgeAsync(
            new Wire.AgentRequest { Agent = infra.Name }, cancellationToken: token);

        Assert.Equal(Wire.PurgeVerdict.Gone, purged.Verdict);

        Assert.True(
            purged.Destroyed.Sum(toll => toll.Rows) > 0,
            "a purge that reported destroying nothing, for an agent that had sent and received");

        var after = await ConversationsAsync(operators, token);

        // **All of what it names.**
        Assert.DoesNotContain(doomed.ConversationId, after);

        // **And none of what it does not.**
        Assert.Contains(spared.ConversationId, after);

        // The spared conversation is not merely listed — it still works, which is the half a listing
        // cannot show. A half-deleted neighbour would list and then refuse.
        _ = await billing.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: token);

        Assert.True(
            (await billing.Client.ResultAsync(
                Requests.Result(billing.Name, spared.ExchangeId), cancellationToken: token)).Ok,
            "the conversation the purge should not have touched could no longer be answered");
    }

    /// <summary>Every conversation the broker holds, by identifier.</summary>
    /// <param name="operators">The operator surface.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifiers.</returns>
    private static async Task<IReadOnlyCollection<string>> ConversationsAsync(
        Wire.Operators.OperatorsClient operators, CancellationToken cancellationToken)
    {
        var listed = await operators.ListConversationsAsync(
            new Wire.ListConversationsRequest { IncludeArchived = true },
            cancellationToken: cancellationToken);

        return [.. listed.Conversations.Select(conversation => conversation.ConversationId)];
    }
}
