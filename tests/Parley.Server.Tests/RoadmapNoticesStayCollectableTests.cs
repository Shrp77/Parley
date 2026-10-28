using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That an agent's inbox stays collectable after a revision supersedes many of its proposals, and tells each once.
/// </summary>
/// <remarks>
/// <b>Found by review, round 19.</b> A notice carried its proposal's whole title, which has no bound, and a collection
/// drains fifty at once and marks them delivered before the reply is sent: fifty long titles exceeded what a reply may
/// carry, so the collection failed, and everything it had taken, another agent's Request among them, was never shown.
/// A notice's title is now cut as a heading's is.
/// </remarks>
public sealed class RoadmapNoticesStayCollectableTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.RoadmapContent Plan(string title) => new()
    {
        Title = title,
        Items = { new Wire.RoadmapItemDraft { Text = "Add CSV export", IntendedAgent = "billing@test" } },
    };

    [Fact]
    public async Task A_decline_racing_a_drain_is_told_exactly_once()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        for (var round = 0; round < 40; round++)
        {
            var proposed = await orders.Client.ProposeRoadmapAsync(
                new Wire.ProposeRoadmapRequest { Agent = orders.Name, Content = Plan($"Plan {round}") }, cancellationToken: Ct);

            var declining = operators.DeclineProposalAsync(new Wire.ProposalRequest { Id = proposed.ProposalId }, cancellationToken: Ct);
            var draining = orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);
            var draining2 = orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

            Assert.True((await declining.ResponseAsync).Done);
            var told = (await draining).RoadmapNotices.Count(n => n.ProposalId == proposed.ProposalId)
                + (await draining2).RoadmapNotices.Count(n => n.ProposalId == proposed.ProposalId)
                + (await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct))
                    .RoadmapNotices.Count(n => n.ProposalId == proposed.ProposalId);

            Assert.True(told == 1, $"round {round}: told {told} times");
        }
    }

    [Fact]
    public async Task A_supersede_of_many_long_titled_proposals_leaves_the_inbox_collectable_and_loses_no_request()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        // A roadmap to revise, and its own notice collected.
        var first = await orders.Client.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest { Agent = orders.Name, Content = Plan("Billing export") }, cancellationToken: Ct);
        var confirmed = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = first.ProposalId, Content = Plan("Billing export") }, cancellationToken: Ct);
        Assert.True(confirmed.Done, confirmed.Problem);
        _ = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

        // Fifty revisions, each with a 100,000-character title: roadmap text has no bound, and each request is well
        // under gRPC's 4 MB.
        var ids = new List<long>();

        for (var n = 0; n < 50; n++)
        {
            var reply = await orders.Client.ProposeRoadmapAsync(
                new Wire.ProposeRoadmapRequest
                {
                    Agent = orders.Name,
                    Content = Plan(new string('a', 100_000) + n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Revises = "Billing export",
                },
                cancellationToken: Ct);
            Assert.Empty(reply.Problem);
            ids.Add(reply.ProposalId);
        }

        // The person confirms one as the revision: the other 49 are superseded, and orders is told of all 50.
        var revised = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = ids[0], Content = Plan("Billing export v2"), TargetRoadmap = confirmed.RoadmapId },
            cancellationToken: Ct);
        Assert.True(revised.Done, revised.Problem);

        // Meanwhile billing asks orders something.
        var asked = Assert.Single((await billing.Client.RequestAsync(
            Requests.Open(billing.Name, orders.Name, "what is the export column called?"), cancellationToken: Ct)).Outcomes).ExchangeId;

        string? failed = null;

        try
        {
            _ = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);
        }
        catch (RpcException failure)
        {
            failed = $"{failure.StatusCode}: {failure.Status.Detail}";
        }

        var again = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

        Assert.True(
            failed is null || again.Messages.Any(m => m.Message.ExchangeId == asked),
            $"the collection failed ({failed}) and billing's request is not in the next one: messages {again.Messages.Count}, " +
            $"roadmap notices {again.RoadmapNotices.Count}, waiting {again.RoadmapNoticesWaiting}");
    }
}
