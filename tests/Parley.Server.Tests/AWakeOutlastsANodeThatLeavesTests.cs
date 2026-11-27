using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the wakes a broker's start owes a machine are still paid when its node leaves inside the wait before them and joins
/// again afterwards: the start a lapsed freeze owes, and the restart wake (SPEC.md §14).
/// </summary>
/// <remarks>
/// <para>
/// Both are taken at the join (the lapse off the broker's list, the restart wake by claiming the machine), so a node that
/// left while the wake waited had nobody to ask. Each is given back for the next join: without that, the next join paid
/// nothing, and the work or the answer waited for something unrelated to arrive. A node rejoins whenever its catalogue
/// changes, so a join and a leave inside five seconds is an ordinary enrolment after a restart.
/// </para>
/// <para>Review, lens 3 run 1 on 6fde4ef3: each give-back passed every test with it removed.</para>
/// </remarks>
[Integration]
public sealed class AWakeOutlastsANodeThatLeavesTests : IDisposable
{
    private const string Here = "m1";
    private const string Billing = "billing@m1";
    private const string Orders = "orders@m1";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-wake-node-left").FullName;

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
    public async Task A_lapse_whose_node_leaves_inside_the_wait_is_paid_when_it_joins_again()
    {
        await FrozenWithMailAsync();

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait);

        await JoinAndLeaveAsync(second, ["billing"]);

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["billing"], Ct, reportsTurns: true);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(400), Patience));
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, node.Commands[0].CommandCase);
        Assert.Equal(Billing, node.Commands[0].Start.Agent);
    }

    [Fact]
    public async Task A_restart_wake_whose_node_leaves_inside_the_wait_is_asked_for_when_it_joins_again()
    {
        await using var broker = await LoopbackBroker.StartAsync(
            Ct,
            options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always },
            restartWakeDelay: Wait);

        // Orders owes work and has no process.
        await using (await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);

        await using (var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct))
        {
            Assert.True(Assert.Single((await docs.Client.RequestAsync(
                Requests.Open("docs@m1", Orders, "owed by nobody running", "c1"), cancellationToken: Ct)).Outcomes).Ok);
        }

        await JoinAndLeaveAsync(broker, ["orders"]);

        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(400), Patience));
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, node.Commands[0].CommandCase);
        Assert.Equal(Orders, node.Commands[0].Start.Agent);
    }

    // Joined and gone before the wait is over, so the wake finds nobody to ask; returns once that wake has finished.
    private static async Task JoinAndLeaveAsync(LoopbackBroker broker, string[] startable)
    {
        await using (var gone = await JoinedNodeClient.JoinAsync(broker, Here, startable, Ct, reportsTurns: true, waitForRestartWake: false))
        {
        }

        await broker.WaitUntilNodeGoneAsync(Here, Patience);
        await broker.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);
    }

    // Frozen by the person (not kept) and written to while frozen, with nothing of it running.
    private async Task FrozenWithMailAsync()
    {
        await using var first = await LoopbackBroker.StartOnAsync(Store, Ct);
        await using var docs = await AttachedClient.AttachAsync(first, "docs@m1", cancellationToken: Ct);

        await using (var billing = await AttachedClient.AttachAsync(first, Billing, cancellationToken: Ct))
        {
        }

        await first.WaitUntilDetachedAsync(Billing, Patience);

        Assert.True((await first.ConnectAsOperator().FreezeAsync(
            new Wire.FreezeRequest { Agent = Billing }, cancellationToken: Ct)).Changed);

        var sent = Assert.Single((await docs.Client.RequestAsync(
            Requests.Open("docs@m1", Billing, "while frozen", "c1"), cancellationToken: Ct)).Outcomes);

        Assert.True(sent.Ok);
        Assert.Equal(Wire.WakeOutcome.Frozen, sent.Wake);
    }
}
