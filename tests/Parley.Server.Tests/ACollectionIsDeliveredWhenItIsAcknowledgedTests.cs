using System.Globalization;
using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a collection is recorded delivered only once its caller acknowledges it, and is handed over again when the call
/// ends first (SPEC.md §6).
/// </summary>
/// <remarks>
/// A collection was stamped delivered as the broker built it, so a caller that gave up part way, a person pressing Esc
/// during the inbox tool among them, lost every message it had been handed: a probe lost 720 of 2,700 Results, and none
/// came back. Each case here is a way a reply can fail to reach its caller.
/// </remarks>
public sealed class ACollectionIsDeliveredWhenItIsAcknowledgedTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A collection read and then left without its acknowledgement, as a caller that went away does.
    private static async Task<Wire.InboxReply> CollectWithoutAcknowledgingAsync(AttachedClient client)
    {
        using var call = client.Client.DrainInbox(cancellationToken: Ct);
        await call.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Collect = new Wire.DrainInboxRequest { Agent = client.Name } }, Ct);
        Assert.True(await call.ResponseStream.MoveNext(Ct));
        var reply = call.ResponseStream.Current;
        await call.RequestStream.CompleteAsync();

        // The broker ends the call without recording anything.
        Assert.False(await call.ResponseStream.MoveNext(Ct));

        return reply;
    }

    [Fact]
    public async Task A_collection_never_acknowledged_is_handed_over_again()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        _ = await billing.Client.RequestAsync(Requests.Open(Billing, Orders, "net or gross?"), cancellationToken: Ct);

        var lost = await CollectWithoutAcknowledgingAsync(orders);
        var again = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        var after = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.Equal(Assert.Single(lost.Messages).Message.Id, Assert.Single(again.Messages).Message.Id);
        Assert.Empty(after.Messages);
    }

    [Fact]
    public async Task A_second_collection_does_not_take_what_one_in_flight_holds()
    {
        // Two sessions of one agent: while the first's reply is on its way, the second must not be handed the same work.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        _ = await billing.Client.RequestAsync(Requests.Open(Billing, Orders, "net or gross?"), cancellationToken: Ct);

        using var first = orders.Client.DrainInbox(cancellationToken: Ct);
        await first.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Collect = new Wire.DrainInboxRequest { Agent = Orders } }, Ct);
        Assert.True(await first.ResponseStream.MoveNext(Ct));
        Assert.Single(first.ResponseStream.Current.Messages);

        var meanwhile = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        await first.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Acknowledge = new Wire.InboxAcknowledgement() }, Ct);
        await first.RequestStream.CompleteAsync();
        Assert.False(await first.ResponseStream.MoveNext(Ct));

        var afterwards = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.Empty(meanwhile.Messages);
        Assert.Empty(afterwards.Messages);
    }

    [Fact]
    public async Task A_gone_notice_in_a_collection_never_acknowledged_is_told_again()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", "net 30"u8.ToArray(), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, "terms.md", "pricing", Ct);
        _ = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "pricing", Name = "terms.md" } } },
            cancellationToken: Ct);

        var lost = await CollectWithoutAcknowledgingAsync(orders);
        var again = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        var after = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.Equal("terms.md", Assert.Single(lost.FileNotices).Name);
        Assert.Equal("terms.md", Assert.Single(again.FileNotices).Name);
        Assert.Empty(after.FileNotices);
    }

    [Fact]
    public async Task A_roadmap_notice_in_a_collection_never_acknowledged_is_told_again()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var content = new Wire.RoadmapContent { Title = "Billing export" };
        content.Items.Add(new Wire.RoadmapItemDraft { Text = "Add CSV export", IntendedAgent = Billing });
        var proposed = await orders.Client.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest { Agent = Orders, Content = content }, cancellationToken: Ct);
        Assert.True(proposed.ProposalId > 0, proposed.Problem);
        _ = await broker.ConnectAsOperator().DeclineProposalAsync(
            new Wire.ProposalRequest { Id = proposed.ProposalId }, cancellationToken: Ct);

        var lost = await CollectWithoutAcknowledgingAsync(orders);
        var again = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        var after = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.Equal(proposed.ProposalId, Assert.Single(lost.RoadmapNotices).ProposalId);
        Assert.Equal(proposed.ProposalId, Assert.Single(again.RoadmapNotices).ProposalId);
        Assert.Empty(after.RoadmapNotices);
    }

    [Fact]
    public async Task A_collection_cancelled_at_any_point_loses_no_result()
    {
        // Given up after a few milliseconds, so the cancel lands somewhere different in each round: before the reply or
        // while it arrives. One landing after the reply has arrived is ignored, which the client exists to do. A probe of timing, so it catches a broker that records
        // before the acknowledgement most runs rather than every run; A_collection_never_acknowledged_is_handed_over_again
        // holds that rule on every run.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, "inventory@test", cancellationToken: Ct);

        var sent = 0;
        var cancelled = 0;
        var seen = new HashSet<long>();

        for (var round = 0; round < 18; round++)
        {
            var exchanges = new List<long>();

            for (var n = 0; n < 80; n++)
            {
                var asked = Assert.Single((await orders.Client.RequestAsync(
                    Requests.Open(Orders, inventory.Name, $"q{round.ToString(CultureInfo.InvariantCulture)}-{n.ToString(CultureInfo.InvariantCulture)}"),
                    cancellationToken: Ct)).Outcomes);
                exchanges.Add(asked.ExchangeId);
            }

            await DrainAllAsync(inventory, []);

            foreach (var id in exchanges)
            {
                _ = await inventory.Client.ResultAsync(Requests.Result(inventory.Name, id), cancellationToken: Ct);
                sent++;
            }

            using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                // Every third round holds the store's turn so the collection is certainly in flight when it is given up;
                // the rest give up after a few milliseconds, landing wherever the timing puts them.
                var release = new TaskCompletionSource();
                var occupied = round % 3 == 0
                    ? broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct)
                    : Task.FromResult(true);

                var collecting = orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: giveUp.Token);

                if (round % 3 == 0)
                {
                    await Task.Delay(100, Ct);
                    await giveUp.CancelAsync();
                }
                else
                {
                    giveUp.CancelAfter(TimeSpan.FromMilliseconds(1 + (round % 6)));
                }

                try
                {
                    var reply = await collecting;
                    seen.UnionWith(reply.Messages.Select(m => m.Message.Id));
                }
                catch (Exception gone) when (gone is RpcException { StatusCode: StatusCode.Cancelled } or OperationCanceledException)
                {
                    cancelled++;
                }

                release.TrySetResult();
                await occupied;
            }

            // Long enough for the broker to see the call end and release what it held.
            await Task.Delay(300, Ct);
            await DrainAllAsync(orders, seen);
        }

        // Some collections must have been given up, or this held nothing.
        Assert.True(cancelled > 0, "no collection was cancelled");
        Assert.Equal(sent, seen.Count);
    }

    private static async Task DrainAllAsync(AttachedClient client, HashSet<long> seen)
    {
        Wire.InboxReply drained;

        do
        {
            drained = await client.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = client.Name }, cancellationToken: Ct);
            seen.UnionWith(drained.Messages.Select(m => m.Message.Id));
        }
        while (drained.MessagesWaiting > 0);
    }
}
