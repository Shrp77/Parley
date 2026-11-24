using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a lapsed freeze whose start was passed over because the agent ran as a left process stays in the store until that
/// process has gone and the start has been asked for, so a broker restarted meanwhile still owes it (SPEC.md §14, §11.1).
/// </summary>
/// <remarks>
/// Review, lens 2 run C on 5de89187: a wake passed over as left running counted as paid, the stored lapse was cleared, and a
/// restart before the left process ended lost the start (0 starts, against 1 without the restart).
/// </remarks>
[Integration]
public sealed class ALapseOwedToALeftProcessOutlivesARestartTests : IDisposable
{
    private const string Here = "m1";
    private const string Orders = "orders@m1";
    private const string Billing = "billing@m1";
    private const string Gone = "from-a-node-that-is-gone";
    private const int LeftProcess = 4242;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-on-left").FullName;

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
    public async Task A_restart_before_the_left_process_ends_still_starts_the_agent_once_it_goes()
    {
        var options = BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always };

        await FrozenWithAResultWaitingAsync(options);

        // The freeze lapses; orders' old process attaches inside the wait, so the lapse's start is passed over. Broker B then
        // stops gracefully while that process is still attached: its detach finds no node to ask, which pays nothing.
        await PassedOverAsync(options);

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options);
        await using var node = await JoinedNodeClient.JoinAsync(third, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);
        var left = await AttachedClient.AttachAsync(third, Orders, cancellationToken: Ct, launchToken: Gone, processId: LeftProcess);

        await third.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        // Not started beside itself.
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));

        // It finishes its turn and exits without collecting: the lapse is paid now.
        await left.DisposeAsync();
        await third.WaitUntilDetachedAsync(Orders, Patience);

        // Waited for, then listened past for a second one: a fixed window cannot tell "not yet" from "never" (CountCommandsAsync).
        _ = await node.CountCommandsAsync(1, Wait, Patience);

        Assert.Equal(1, node.Commands.Count(StartOf(Orders)));
    }

    [Fact]
    public async Task A_persons_kill_of_the_left_process_settles_the_lapse_across_a_restart()
    {
        // Never, so the restart wake starts nobody and only a lapse still stored could start orders afterwards.
        var options = BrokerOptions.Default with { ResumePolicy = ResumePolicy.Never };

        await FrozenWithAResultWaitingAsync(options);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options))
        {
            await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);
            var left = await AttachedClient.AttachAsync(second, Orders, cancellationToken: Ct, launchToken: Gone, processId: LeftProcess);

            await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

            Assert.True((await second.ConnectAsOperator().KillAsync(
                new Wire.StopRequest { Agent = Orders, Because = "stuck" }, cancellationToken: Ct)).Changed);

            // The node ends it, and its adapter goes with it.
            await left.DisposeAsync();
            await second.WaitUntilDetachedAsync(Orders, Patience);

            _ = await node.CountCommandsAsync(1, Wait, Patience);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Kill, Assert.Single(node.Commands).CommandCase);
        }

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options);
        await using var again = await JoinedNodeClient.JoinAsync(third, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);

        await third.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        Assert.Equal(0, await again.CountCommandsAsync(TimeSpan.FromSeconds(1)));
    }

    // Review, lens 3 run A on 81b6a1a7: with the store left holding a lapse the left process's detach had paid, every
    // test here stayed green, and each later start of the broker started the agent again for work it had been asked for.
    [Fact]
    public async Task A_lapse_paid_when_the_left_process_goes_is_not_asked_again_by_the_next_start()
    {
        // Never, so the restart wake starts nobody and only a lapse still stored could start orders afterwards.
        var options = BrokerOptions.Default with { ResumePolicy = ResumePolicy.Never };

        await FrozenWithAResultWaitingAsync(options);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options))
        {
            await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);
            var left = await AttachedClient.AttachAsync(second, Orders, cancellationToken: Ct, launchToken: Gone, processId: LeftProcess);

            await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

            Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));

            // It exits without collecting: the lapse's start is asked now, and the lapse is paid.
            await left.DisposeAsync();
            await second.WaitUntilDetachedAsync(Orders, Patience);

            _ = await node.CountCommandsAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, node.Commands.Count(StartOf(Orders)));
        }

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options);
        await using var again = await JoinedNodeClient.JoinAsync(third, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);

        await third.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        Assert.Equal(0, await again.CountCommandsAsync(TimeSpan.FromSeconds(1)));
    }

    // The same when the node announces the process again (TakenBack) rather than the process going: the turn it is asked
    // pays the lapse, so the store does not keep it for the next start.
    [Fact]
    public async Task A_lapse_paid_when_its_node_takes_the_process_back_is_not_asked_again_by_the_next_start()
    {
        var options = BrokerOptions.Default with { ResumePolicy = ResumePolicy.Never };

        await FrozenWithAResultWaitingAsync(options);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options))
        {
            await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);
            var process = await AttachedClient.AttachAsync(second, Orders, cancellationToken: Ct, launchToken: Gone, processId: LeftProcess);

            await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

            Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));

            _ = await node.Nodes.ReportLaunchAsync(
                new Wire.LaunchReport { Machine = Here, Agent = Orders, Token = Gone }, cancellationToken: Ct);

            // The passed-over start is asked of the process it now supervises, as a turn.
            _ = await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(500), Patience);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, Assert.Single(node.Commands).CommandCase);

            await process.DisposeAsync();
            await second.WaitUntilDetachedAsync(Orders, Patience);
        }

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options);
        await using var again = await JoinedNodeClient.JoinAsync(third, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);

        await third.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        Assert.Equal(0, await again.CountCommandsAsync(TimeSpan.FromSeconds(1)));
    }

    private static Func<Wire.NodeCommand, bool> StartOf(string agent) =>
        command => command.CommandCase == Wire.NodeCommand.CommandOneofCase.Start && command.Start.Agent == agent;

    // Broker A: orders asks billing and goes; the person freezes orders (not kept); billing answers, so a Result waits for a
    // frozen agent and its wake is declined.
    private async Task FrozenWithAResultWaitingAsync(BrokerOptions options)
    {
        await using var first = await LoopbackBroker.StartOnAsync(Store, Ct, options: options);
        await using var billing = await AttachedClient.AttachAsync(first, Billing, cancellationToken: Ct);

        long exchange;

        await using (var orders = await AttachedClient.AttachAsync(first, Orders, cancellationToken: Ct))
        {
            var asked = Assert.Single((await orders.Client.RequestAsync(
                Requests.Open(Orders, Billing, "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

            Assert.True(asked.Ok);
            exchange = asked.ExchangeId;
        }

        await first.WaitUntilDetachedAsync(Orders, Patience);

        Assert.True((await first.ConnectAsOperator().FreezeAsync(
            new Wire.FreezeRequest { Agent = Orders }, cancellationToken: Ct)).Changed);

        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);

        Assert.True((await billing.Client.ResultAsync(Requests.Result(Billing, exchange), cancellationToken: Ct)).Ok);
    }

    // Broker B: the lapse's start is passed over for the left process, then the broker stops before that process ends.
    private async Task PassedOverAsync(BrokerOptions options)
    {
        var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait, options: options);
        var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders", "billing"], Ct, reportsTurns: true, waitForRestartWake: false);
        var left = await AttachedClient.AttachAsync(second, Orders, cancellationToken: Ct, launchToken: Gone, processId: LeftProcess);

        await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));

        // The broker first, so the left process is still attached when it stops.
        await second.DisposeAsync();
        await left.DisposeAsync();
        await node.DisposeAsync();
    }
}
