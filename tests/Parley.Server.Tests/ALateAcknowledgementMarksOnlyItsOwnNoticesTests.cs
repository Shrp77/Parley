using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a collection acknowledged late marks delivered only the roadmap notices it handed over (SPEC.md §6).
/// </summary>
/// <remarks>
/// <b>Found by review, round 53.</b> Notices were marked by id alone, and a notice's id is reused once its row goes: a
/// collection held across a purge of its agent, acknowledged after another agent's proposal was declined, marked that
/// agent's new notice, under the reused id, delivered, so it was never told.
/// </remarks>
[Integration]
public sealed class ALateAcknowledgementMarksOnlyItsOwnNoticesTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";
    private const string Inventory = "inventory@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_purged_agents_late_acknowledgement_leaves_another_agents_notice_waiting()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await DeclinedAsync(orders.Client, Orders, operators);

        using var call = orders.Client.DrainInbox(cancellationToken: Ct);
        await call.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Collect = new Wire.DrainInboxRequest { Agent = Orders } }, Ct);
        Assert.True(await call.ResponseStream.MoveNext(Ct));
        Assert.Single(call.ResponseStream.Current.RoadmapNotices);

        // The purge deletes the notice the collection holds, so the next one written takes its id.
        _ = await operators.PurgeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);
        await DeclinedAsync(inventory.Client, Inventory, operators);

        await call.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Acknowledge = new Wire.InboxAcknowledgement() }, Ct);
        await call.RequestStream.CompleteAsync();

        try
        {
            while (await call.ResponseStream.MoveNext(Ct))
            {
            }
        }
        catch (Grpc.Core.RpcException)
        {
            // What becomes of the purged agent's call is not the question.
        }

        var collected = await inventory.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Inventory }, cancellationToken: Ct);

        Assert.Single(collected.RoadmapNotices);
    }

    private static async Task DeclinedAsync(Wire.Agents.AgentsClient agent, string name, Wire.Operators.OperatorsClient operators)
    {
        var content = new Wire.RoadmapContent { Title = name + " plan" };
        content.Items.Add(new Wire.RoadmapItemDraft { Text = "Add CSV export", IntendedAgent = Billing });

        var proposed = await agent.ProposeRoadmapAsync(new Wire.ProposeRoadmapRequest { Agent = name, Content = content }, cancellationToken: Ct);
        Assert.True(proposed.ProposalId > 0, proposed.Problem);

        _ = await operators.DeclineProposalAsync(new Wire.ProposalRequest { Id = proposed.ProposalId }, cancellationToken: Ct);
    }
}
