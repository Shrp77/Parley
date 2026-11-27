using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a broker restarted under a process its node started does not start a second one beside it (SPEC.md §11.1, §14).
/// </summary>
/// <remarks>
/// Review with real processes, reproduced 2 of 2: <c>serve</c> was killed while an agent's process owed a Request it had
/// collected, and restarted. The node rejoined about 70 ms later and the restart wake, run at the join, found the agent
/// detached, because its adapter tries again only every two seconds. It started a second process in the same checkout,
/// both collected the same work, and the person could not kill the first. The wake now waits for processes that outlived
/// the broker to attach again; one back by then presents a token no node holds, reads as left running, and is skipped.
/// </remarks>
[Integration]
public sealed class ARestartWakeWaitsForLeftProcessesTests
{
    private const string Here = "m1";
    private const string Left = "billing@m1";
    private const string Owing = "orders@m1";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Registered and detached, as an agent that ran before and has no process now.
    private static async Task RegisterAsync(LoopbackBroker broker, string name)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: Ct))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    [Fact]
    public async Task A_process_that_attaches_again_inside_the_wait_is_not_started_a_second_time()
    {
        await using var broker = await LoopbackBroker.StartAsync(
            Ct,
            options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always },
            restartWakeDelay: BrokerRuntime.RestartWakeDelayDefault);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        // Before the restart: a node that is gone started billing, which collected a Request and has not answered it.
        await using (var before = await AttachedClient.AttachAsync(
            broker, Left, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242))
        {
            Assert.True(Assert.Single((await docs.Client.RequestAsync(
                Requests.Open("docs@m1", Left, "owed by the old process", "c1"), cancellationToken: Ct)).Outcomes).Ok);
            Assert.Single((await before.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = Left }, cancellationToken: Ct)).Messages);
        }

        // The broker went away under it, so its adapter is detached until it tries again.
        await broker.WaitUntilDetachedAsync(Left, Patience);

        // Orders owes work and has no process: the wake's start for it is what says the wake has run.
        await RegisterAsync(broker, Owing);
        Assert.True(Assert.Single((await docs.Client.RequestAsync(
            Requests.Open("docs@m1", Owing, "owed by nobody running", "c2"), cancellationToken: Ct)).Outcomes).Ok);

        // The node rejoins first, as it did, and the old adapter attaches again after it.
        await using var node = await JoinedNodeClient.JoinAsync(
            broker, Here, ["billing", "orders"], Ct, reportsTurns: true, waitForRestartWake: false);
        await using var again = await AttachedClient.AttachAsync(
            broker, Left, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242);

        await broker.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Owing, command.Start.Agent);

        // Nothing for billing: its old process is back, and a second would put two models in one checkout.
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Briefly));

        var row = (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct))
            .Rows.Single(each => each.Name == Left);

        Assert.True(row.Unsupervised, "the old process should read as left running");
    }

    [Fact]
    public async Task Stopping_the_broker_ends_a_wake_still_waiting_without_a_fault()
    {
        var broker = await LoopbackBroker.StartAsync(
            Ct,
            options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always },
            restartWakeDelay: TimeSpan.FromHours(1));

        Task wake;

        try
        {
            await RegisterAsync(broker, Owing);

            await using (var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct))
            {
                Assert.True(Assert.Single((await docs.Client.RequestAsync(
                    Requests.Open("docs@m1", Owing, "owed by nobody running", "c1"), cancellationToken: Ct)).Outcomes).Ok);
            }

            await using var node = await JoinedNodeClient.JoinAsync(
                broker, Here, ["orders"], Ct, waitForRestartWake: false);

            wake = broker.Runtime.RestartWakeOf(Here);

            Assert.False(wake.IsCompleted, "the wake should still be waiting");
        }
        catch
        {
            await broker.DisposeAsync();
            throw;
        }

        // Stopped with the broker rather than left to fire an hour later against a closed store, or holding the stop until
        // it does, and never faulted.
        await broker.DisposeAsync().AsTask().WaitAsync(Patience, Ct);
        Assert.True(wake.IsCompletedSuccessfully);
    }
}
