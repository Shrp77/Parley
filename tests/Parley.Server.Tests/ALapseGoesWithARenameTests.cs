using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the start a lapsed freeze owes goes with the agent when it is renamed before its node joins (SPEC.md §14, §4.1.1).
/// </summary>
/// <remarks>
/// Review, lens 3 on fc26cb8a: the rename carries the lapse from the old name to the new, and every test passed with that
/// carry removed. Without it the answer that arrived while the agent was frozen waits for something unrelated.
/// </remarks>
[Integration]
public sealed class ALapseGoesWithARenameTests : IDisposable
{
    private const string Here = "alpha";
    private const string Orders = "orders@alpha";
    private const string Sales = "sales@alpha";
    private const string Billing = "billing@alpha";
    private const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";
    private const string Workspace = "/work/orders";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(400);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-rename").FullName;

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
    public async Task A_lapse_owed_under_the_old_name_is_paid_under_the_new_one()
    {
        // The first run: orders asks billing, is frozen with nothing running, and billing answers.
        await using (var first = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await using var billing = await AttachedClient.AttachAsync(first, Billing, cancellationToken: Ct);
            long exchange;

            await using (var orders = await AttachedClient.AttachAsync(
                first, Orders, workspace: Workspace, cancellationToken: Ct, identity: Id))
            {
                var asked = Assert.Single((await orders.Client.RequestAsync(
                    Requests.Open(Orders, Billing, "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

                Assert.True(asked.Ok, asked.Rejection.ToString());
                exchange = asked.ExchangeId;
            }

            await first.WaitUntilDetachedAsync(Orders, Patience);

            Assert.True((await first.ConnectAsOperator().FreezeAsync(
                new Wire.FreezeRequest { Agent = Orders }, cancellationToken: Ct)).Changed);

            _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);
            Assert.True((await billing.Client.ResultAsync(Requests.Result(Billing, exchange), cancellationToken: Ct)).Ok);
        }

        // The second run lapses the freeze; the agent is opened once under a new name, which renames it, and closed.
        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        await using (var renamed = await AttachedClient.AttachAsync(
            second, Sales, workspace: Workspace, cancellationToken: Ct, identity: Id))
        {
        }

        await second.WaitUntilDetachedAsync(Sales, Patience);

        Assert.Contains(
            (await second.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows,
            row => row.Name == Sales);

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders", "sales"], Ct, reportsTurns: true);

        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Sales, command.Start.Agent);
        Assert.Contains("freeze lapsed", command.Start.Because, StringComparison.Ordinal);
    }
}
