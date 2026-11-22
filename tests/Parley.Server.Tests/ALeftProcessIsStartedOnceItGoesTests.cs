using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That what a wake passed over because an agent was left running is asked for once that process has gone (SPEC.md §11.1,
/// §14), and that a freeze or a person's kill still holds.
/// </summary>
/// <remarks>
/// Review, lens 1 run C, Linux, real processes, 5 of 5: a broker that died hard under an agent's turn found its process
/// attached again with a token no node holds, and the restart wake skipped it as it should. The process finished its turn,
/// found its input ended and exited, and nothing started the agent again: the exchange it had collected stayed open, and
/// mail sent to it meanwhile was stranded too, its sender told an editor would prompt it.
/// </remarks>
[Integration]
public sealed class ALeftProcessIsStartedOnceItGoesTests
{
    private const string Here = "m1";
    private const string Billing = "billing@m1";
    private const string Docs = "docs@m1";
    private const string Gone = "from-a-node-that-is-gone";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Work_it_collected_and_left_open_starts_it_once_when_the_left_process_goes()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await OwedByAProcessThatOutlivedTheBrokerAsync(broker, docs);

        var (node, again) = await RestartedUnderTheLeftProcessAsync(broker);

        await using (node)
        {
            Assert.Equal(0, await node.CountCommandsAsync(Briefly));

            // It finishes its turn, finds its input ended, and exits.
            await again.DisposeAsync();
            await broker.WaitUntilDetachedAsync(Billing, Patience);

            Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromSeconds(1), Patience));

            var command = Assert.Single(node.Commands);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
            Assert.Equal(Billing, command.Start.Agent);
        }
    }

    [Fact]
    public async Task Mail_sent_while_it_ran_starts_it_once_when_the_left_process_goes_and_its_sender_is_told_so()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        var (node, again) = await RestartedUnderTheLeftProcessAsync(broker);

        await using (node)
        {
            var sent = Assert.Single((await docs.Client.RequestAsync(
                Requests.Open(Docs, Billing, "sent while the old process runs", "c1"), cancellationToken: Ct)).Outcomes);

            Assert.True(sent.Ok);

            // Not "open outside Parley": nobody has a left process open to prompt it.
            Assert.Equal(Wire.WakeOutcome.LeftRunning, sent.Wake);
            Assert.Equal(0, await node.CountCommandsAsync(Briefly));

            await again.DisposeAsync();
            await broker.WaitUntilDetachedAsync(Billing, Patience);

            Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromSeconds(1), Patience));

            var command = Assert.Single(node.Commands);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
            Assert.Equal(Billing, command.Start.Agent);
        }
    }

    [Fact]
    public async Task A_frozen_agent_is_not_started_when_the_left_process_goes()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await OwedByAProcessThatOutlivedTheBrokerAsync(broker, docs);

        var (node, again) = await RestartedUnderTheLeftProcessAsync(broker);

        await using (node)
        {
            Assert.True((await broker.ConnectAsOperator().FreezeAsync(
                new Wire.FreezeRequest { Agent = Billing }, cancellationToken: Ct)).Changed);

            await again.DisposeAsync();
            await broker.WaitUntilDetachedAsync(Billing, Patience);

            Assert.DoesNotContain(
                await CommandsAfterAsync(node), command => command.CommandCase == Wire.NodeCommand.CommandOneofCase.Start);
        }
    }

    [Fact]
    public async Task A_persons_kill_starts_nothing_when_the_left_process_goes()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await OwedByAProcessThatOutlivedTheBrokerAsync(broker, docs);

        var (node, again) = await RestartedUnderTheLeftProcessAsync(broker);

        await using (node)
        {
            Assert.True((await broker.ConnectAsOperator().KillAsync(
                new Wire.StopRequest { Agent = Billing, Because = "stuck" }, cancellationToken: Ct)).Changed);

            // The node ends it, and its adapter goes with it.
            await again.DisposeAsync();
            await broker.WaitUntilDetachedAsync(Billing, Patience);

            var commands = await CommandsAfterAsync(node);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Kill, Assert.Single(commands).CommandCase);
        }
    }

    private static Task<LoopbackBroker> StartAsync() =>
        LoopbackBroker.StartAsync(
            Ct,
            options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always },
            restartWakeDelay: TimeSpan.FromSeconds(1));

    // Before the restart: a node that is gone started billing, which collected a Request and has not answered it. The broker
    // then went away under it, so its adapter is detached until it tries again.
    private static async Task OwedByAProcessThatOutlivedTheBrokerAsync(LoopbackBroker broker, AttachedClient docs)
    {
        await using (var before = await AttachedClient.AttachAsync(
            broker, Billing, cancellationToken: Ct, launchToken: Gone, processId: 4242))
        {
            Assert.True(Assert.Single((await docs.Client.RequestAsync(
                Requests.Open(Docs, Billing, "owed by the old process", "c0"), cancellationToken: Ct)).Outcomes).Ok);
            Assert.Single((await before.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).Messages);
        }

        await broker.WaitUntilDetachedAsync(Billing, Patience);
    }

    // The node rejoins first, the old adapter attaches again after it, and the restart wake runs and passes it over.
    private static async Task<(JoinedNodeClient Node, AttachedClient Again)> RestartedUnderTheLeftProcessAsync(LoopbackBroker broker)
    {
        var node = await JoinedNodeClient.JoinAsync(
            broker, Here, ["billing"], Ct, reportsTurns: true, waitForRestartWake: false);
        var again = await AttachedClient.AttachAsync(
            broker, Billing, cancellationToken: Ct, launchToken: Gone, processId: 4242);

        await broker.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        return (node, again);
    }

    // Long enough for a start asked at the detach to have arrived.
    private static async Task<IReadOnlyList<Wire.NodeCommand>> CommandsAfterAsync(JoinedNodeClient node)
    {
        _ = await node.CountCommandsAsync(TimeSpan.FromSeconds(2));

        return node.Commands;
    }
}
