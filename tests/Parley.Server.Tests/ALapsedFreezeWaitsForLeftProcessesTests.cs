using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a freeze the broker's next start lifted is paid only once a process that outlived the broker has had time to attach
/// again, so it is never started a second time beside itself (SPEC.md §14, §11.1).
/// </summary>
/// <remarks>Review, lens 1 run A on d99806d9: the lapse was paid 47 ms after the join, before the left process's adapter was back.</remarks>
[Integration]
public sealed class ALapsedFreezeWaitsForLeftProcessesTests : IDisposable
{
    private const string Here = "m1";
    private const string Billing = "billing@m1";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-left").FullName;

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
    public async Task A_left_process_that_attaches_again_inside_the_wait_is_not_started_beside_itself()
    {
        await FrozenWithMailAsync();

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait);
        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["billing"], Ct, reportsTurns: true, waitForRestartWake: false);

        // Its adapter tries again every two seconds, so it is back after the node.
        await using var again = await AttachedClient.AttachAsync(
            second, Billing, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242);

        await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        Assert.Equal(0, await node.CountCommandsAsync(Wait));
    }

    [Fact]
    public async Task A_lapse_with_no_process_back_is_still_paid_once_after_the_wait()
    {
        await FrozenWithMailAsync();

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait);
        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["billing"], Ct, reportsTurns: true, waitForRestartWake: false);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(400), Patience));
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, node.Commands[0].CommandCase);
    }

    // Left running by a node that is gone, frozen by the person (not kept), and written to while frozen.
    private async Task FrozenWithMailAsync()
    {
        await using var first = await LoopbackBroker.StartOnAsync(Store, Ct);
        await using var docs = await AttachedClient.AttachAsync(first, "docs@m1", cancellationToken: Ct);
        await using var left = await AttachedClient.AttachAsync(
            first, Billing, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242);

        Assert.True((await first.ConnectAsOperator().FreezeAsync(
            new Wire.FreezeRequest { Agent = Billing }, cancellationToken: Ct)).Changed);

        var sent = Assert.Single((await docs.Client.RequestAsync(
            Requests.Open("docs@m1", Billing, "while frozen", "c1"), cancellationToken: Ct)).Outcomes);

        Assert.True(sent.Ok);
        Assert.Equal(Wire.WakeOutcome.Frozen, sent.Wake);
    }
}
