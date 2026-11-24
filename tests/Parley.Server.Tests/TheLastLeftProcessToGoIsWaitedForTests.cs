using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a wake is passed over as left running only when every session is a left process, and that what it passed over is
/// asked for when the last of them goes, not the first (SPEC.md §11.1, §11.7).
/// </summary>
/// <remarks>
/// Review, lens 3 run A on 5de89187, three mutations no test caught. With <c>Ask</c> reading any left session as left
/// running, a sender whose receiver was also open in somebody's editor was told it would be started, not that it reads the
/// message when prompted there (and the broker recursed until its stack overflowed). With <c>ReleaseAsync</c> clearing the
/// note whenever a left process went with another session still attached, the first of two left processes to go cleared
/// it, the second went, and nothing started the agent: the mail sent to it stayed unread, and its sender had been told it
/// would be started once that process ended. With the note paid only by the detach's check for waiting mail, open work the
/// left process had collected was never asked for.
/// </remarks>
[Integration]
public sealed class TheLastLeftProcessToGoIsWaitedForTests
{
    private const string Here = "m1";
    private const string Billing = "billing@m1";
    private const string Docs = "docs@m1";
    private const string Gone = "from-a-node-that-is-gone";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mail_passed_over_starts_the_agent_once_the_second_of_two_left_processes_goes()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(
            broker, Here, ["billing"], Ct, reportsTurns: true, waitForRestartWake: false);
        var first = await AttachedClient.AttachAsync(
            broker, Billing, cancellationToken: Ct, launchToken: Gone, processId: 4242);
        var second = await AttachedClient.AttachAsync(
            broker, Billing, cancellationToken: Ct, launchToken: Gone + "-older", processId: 4343);

        await broker.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        var sent = await SendAsync(docs);

        Assert.Equal(Wire.WakeOutcome.LeftRunning, sent.Wake);

        // The first goes. Another left process still runs, so nothing is started and the note is kept for it.
        await first.DisposeAsync();
        await UntilAsync(() => broker.Runtime.SessionsFor(AgentName.Create(Billing)) == 1);

        Assert.Equal(0, await node.CountCommandsAsync(Briefly));

        // The last goes: what was passed over is asked for now.
        await second.DisposeAsync();
        await broker.WaitUntilDetachedAsync(Billing, Patience);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromSeconds(1), Patience));

        var command = Assert.Single(node.Commands);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Billing, command.Start.Agent);
    }

    [Fact]
    public async Task Work_it_collected_and_left_open_starts_it_when_it_goes_with_nothing_else_waiting()
    {
        // ALeftProcessIsStartedOnceItGoesTests leaves mail waiting, so the detach's ordinary check for waiting mail started
        // the agent with the passed-over note ignored. Here the left process has collected everything: only the note, paid
        // as an unfreeze is, asks for the open work it leaves.
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);

        await using (var before = await AttachedClient.AttachAsync(
            broker, Billing, cancellationToken: Ct, launchToken: Gone, processId: 4242))
        {
            _ = await SendAsync(docs);
            Assert.Single((await before.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).Messages);
        }

        await broker.WaitUntilDetachedAsync(Billing, Patience);

        await using var node = await JoinedNodeClient.JoinAsync(
            broker, Here, ["billing"], Ct, reportsTurns: true, waitForRestartWake: false);
        var again = await AttachedClient.AttachAsync(
            broker, Billing, cancellationToken: Ct, launchToken: Gone, processId: 4242);

        await broker.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        // It collects whatever else reached it before it goes, so nothing is waiting but the work it owes.
        _ = await again.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);
        Assert.Empty((await again.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).Messages);
        Assert.Equal(0, await node.CountCommandsAsync(Briefly));

        await again.DisposeAsync();
        await broker.WaitUntilDetachedAsync(Billing, Patience);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromSeconds(1), Patience));

        var command = Assert.Single(node.Commands);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Billing, command.Start.Agent);
    }

    [Fact]
    public async Task A_left_process_beside_an_editors_session_is_open_outside_Parley_and_asks_nobody()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(
            broker, Here, ["billing"], Ct, reportsTurns: true, waitForRestartWake: false);
        await using var left = await AttachedClient.AttachAsync(
            broker, Billing, cancellationToken: Ct, launchToken: Gone, processId: 4242);
        await using var editor = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await broker.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        var sent = await SendAsync(docs);

        // Somebody has it open and reads it when they prompt it there (§11.7); "started once that process ends" is not so.
        Assert.Equal(Wire.WakeOutcome.OpenOutside, sent.Wake);
        Assert.Equal(0, await node.CountCommandsAsync(Briefly));
    }

    private static Task<LoopbackBroker> StartAsync() =>
        LoopbackBroker.StartAsync(
            Ct,
            options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always },
            restartWakeDelay: TimeSpan.FromSeconds(1));

    private static async Task<Wire.RequestOutcome> SendAsync(AttachedClient docs)
    {
        var sent = Assert.Single((await docs.Client.RequestAsync(
            Requests.Open(Docs, Billing, "sent while an old process runs", "c1"), cancellationToken: Ct)).Outcomes);

        Assert.True(sent.Ok);

        return sent;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the broker did not see the first left process go");
            await Task.Delay(20, Ct);
        }
    }
}
