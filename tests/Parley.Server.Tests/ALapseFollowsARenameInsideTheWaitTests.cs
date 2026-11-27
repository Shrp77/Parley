using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>A lapse owed when its agent is renamed inside the wait is paid under the new name, once (SPEC.md §14).</summary>
/// <remarks>
/// The lapses were taken at the join, under the names they had then: a rename inside the wait left the payment asking for a
/// name with no row, so nothing was started that run and the stored lapse waited for the next start (review, lens 1 run A on
/// fc26cb8a: 16 of 47 rename rounds).
/// </remarks>
[Integration]
public sealed class ALapseFollowsARenameInsideTheWaitTests : IDisposable
{
    private const string Here = "alpha";
    private const string Orders = "orders@alpha";
    private const string Payments = "payments@alpha";
    private const string Billing = "billing@alpha";
    private const string Workspace = "/work/orders";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(400);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-rename-wait").FullName;

    private string Store => Path.Combine(_folder, "parley.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory's to clean.
        }
    }

    [Fact]
    public async Task A_rename_inside_the_wait_is_paid_under_the_new_name_and_clears_the_stored_lapse()
    {
        var identity = Guid.NewGuid().ToString();

        await using (var first = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await using var billing = await AttachedClient.AttachAsync(first, Billing, cancellationToken: Ct);
            long exchange;

            await using (var orders = await AttachedClient.AttachAsync(first, Orders, workspace: Workspace, cancellationToken: Ct, identity: identity))
            {
                var asked = Assert.Single((await orders.Client.RequestAsync(
                    Requests.Open(Orders, Billing, "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);
                Assert.True(asked.Ok);
                exchange = asked.ExchangeId;
            }

            await first.WaitUntilDetachedAsync(Orders, Patience);
            Assert.True((await first.ConnectAsOperator().FreezeAsync(new Wire.FreezeRequest { Agent = Orders }, cancellationToken: Ct)).Changed);
            _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);
            Assert.True((await billing.Client.ResultAsync(Requests.Result(Billing, exchange), cancellationToken: Ct)).Ok);
        }

        int second;

        await using (var broker = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: TimeSpan.FromSeconds(1)))
        {
            await using var node = await JoinedNodeClient.JoinAsync(
                broker, Here, ["orders", "payments"], Ct, reportsTurns: true, waitForRestartWake: false);

            // Renamed by attach (§4.1.1) while the lapse's start waits, then closed again.
            await using (var renamed = await AttachedClient.AttachAsync(broker, Payments, workspace: Workspace, cancellationToken: Ct, identity: identity))
            {
            }

            await broker.WaitUntilDetachedAsync(Payments, Patience);
            await broker.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

            second = await node.CountCommandsAsync(1, Briefly, Patience);
            Assert.All(node.Commands, c => Assert.Equal(Payments, c.Start.Agent));
        }

        int third;

        await using (var broker = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders", "payments"], Ct, reportsTurns: true);
            third = await node.CountCommandsAsync(Briefly);
        }

        // Paid once, in the run it lapsed in, and the stored lapse cleared so no later start pays it again.
        Assert.Equal((1, 0), (second, third));
    }
}
