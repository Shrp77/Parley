using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Many proposals from one agent at once, and proposals racing a revision that supersedes by their hint.
/// </summary>
/// <remarks>
/// Kept from review round 17, where both held: the broker's gate decides them in order, so a proposal is superseded
/// exactly when it was made before the revision, and its proposer is told once.
/// </remarks>
public sealed class ProposalsRacingAConfirmTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.RoadmapContent Plan(string title) => new()
    {
        Title = title,
        Items = { new Wire.RoadmapItemDraft { Text = "Add CSV export", IntendedAgent = "billing@test" } },
    };

    [Fact]
    public async Task Many_proposals_at_once_from_one_agent_are_each_stored_once()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);

        var replies = await Task.WhenAll(Enumerable.Range(0, 64).Select(n => orders.Client.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest { Agent = orders.Name, Content = Plan($"Plan {n}") },
            cancellationToken: Ct).ResponseAsync));

        Assert.All(replies, r => Assert.Empty(r.Problem));
        Assert.Equal(64, replies.Select(r => r.ProposalId).Distinct().Count());

        var count = await broker.ConnectAsOperator().ProposalCountAsync(
            new Wire.ProposalsRequest { State = Wire.ProposalState.Waiting }, cancellationToken: Ct);
        Assert.Equal(64, count.Count);
    }

    [Fact]
    public async Task Proposals_racing_a_revision_with_the_same_hint_are_decided_by_their_order_alone()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        for (var round = 0; round < 10; round++)
        {
            var title = $"Billing export {round}";
            var first = await orders.Client.ProposeRoadmapAsync(
                new Wire.ProposeRoadmapRequest { Agent = orders.Name, Content = Plan(title) }, cancellationToken: Ct);
            var made = await operators.ConfirmProposalAsync(
                new Wire.ConfirmProposalRequest { Id = first.ProposalId, Content = Plan(title) }, cancellationToken: Ct);
            Assert.True(made.Done, made.Problem);

            var revision = await orders.Client.ProposeRoadmapAsync(
                new Wire.ProposeRoadmapRequest { Agent = orders.Name, Content = Plan(title), Revises = title },
                cancellationToken: Ct);

            var racing = Enumerable.Range(0, 20).Select(_ => billing.Client.ProposeRoadmapAsync(
                new Wire.ProposeRoadmapRequest { Agent = billing.Name, Content = Plan("Other"), Revises = title.ToUpperInvariant() },
                cancellationToken: Ct).ResponseAsync).ToList();

            var confirm = operators.ConfirmProposalAsync(
                new Wire.ConfirmProposalRequest
                {
                    Id = revision.ProposalId, Content = Plan(title + " v2"), TargetRoadmap = made.RoadmapId, TargetRevision = made.Revision,
                },
                cancellationToken: Ct).ResponseAsync;

            var ids = (await Task.WhenAll(racing)).Select(r => r.ProposalId).ToList();
            var confirmed = await confirm;
            Assert.True(confirmed.Done, confirmed.Problem);

            var all = (await operators.ProposalsAsync(new Wire.ProposalsRequest(), cancellationToken: Ct)).Proposals
                .Where(p => ids.Contains(p.Id)).ToList();

            Assert.Equal(20, all.Count);
            Assert.All(all, p => Assert.Contains(p.State, new[] { Wire.ProposalState.Waiting, Wire.ProposalState.Superseded }));

            // Everything superseded was made before everything left waiting: a write between the two would show here.
            var superseded = all.Where(p => p.State == Wire.ProposalState.Superseded).Select(p => p.Id).DefaultIfEmpty(0).Max();
            var waiting = all.Where(p => p.State == Wire.ProposalState.Waiting).Select(p => p.Id).DefaultIfEmpty(long.MaxValue).Min();
            Assert.True(superseded < waiting, $"round {round}: superseded up to {superseded}, waiting from {waiting}");

            // Each superseded proposal's proposer is told once, and nobody is told of one left waiting.
            var notices = (await billing.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: Ct)).RoadmapNotices;
            Assert.Equal(all.Count(p => p.State == Wire.ProposalState.Superseded), notices.Count);
            Assert.All(notices, n => Assert.Equal(Wire.ProposalState.Superseded, n.Outcome));

            // Leave nothing waiting for the next round's hint.
            foreach (var left in all.Where(p => p.State == Wire.ProposalState.Waiting))
            {
                await operators.DeclineProposalAsync(new Wire.ProposalRequest { Id = left.Id }, cancellationToken: Ct);
            }

            await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: Ct);
        }
    }
}
