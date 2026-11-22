using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a wake passed over while a process read as left running does not outlive the node taking that process back: the
/// passed-over mail is asked of the supervised process as a turn, and a person's stop afterwards starts nothing (SPEC.md §11.1:
/// a stop forgets the turns asked before it).
/// </summary>
/// <remarks>
/// Review, lens 1 run A on 5de89187: a resident attached again after a broker restart before its node re-announced it
/// (TurnReporter.ReannounceAsync), so it read as left running and a message's wake noted it. The re-announce made it the
/// node's own, the note stayed, and the person's stop was followed at once by a start for the work it had collected.
/// </remarks>
[Integration]
public sealed class AStopForgetsAWakePassedOverTests
{
    private const string Here = "m1";
    private const string Billing = "billing@m1";
    private const string Docs = "docs@m1";
    private const string Token = "t1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_persons_stop_after_the_node_takes_the_process_back_starts_nothing()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        var process = await PassedOverThenTakenBackAsync(broker, docs, node);

        // It collects its work.
        Assert.Single((await process.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).Messages);

        var before = node.Commands.Count;

        Assert.True((await broker.ConnectAsOperator().StopAsync(
            new Wire.StopRequest { Agent = Billing, Because = "a person stops it" }, cancellationToken: Ct)).Changed);

        await process.DisposeAsync();
        await broker.WaitUntilDetachedAsync(Billing, TimeSpan.FromSeconds(10));

        // Long enough for a start asked at the detach to have arrived.
        _ = await node.CountCommandsAsync(TimeSpan.FromSeconds(2));

        var after = node.Commands.Skip(before).Select(command => command.CommandCase).ToList();

        Assert.Contains(Wire.NodeCommand.CommandOneofCase.Stop, after);
        Assert.DoesNotContain(Wire.NodeCommand.CommandOneofCase.Start, after);
    }

    [Fact]
    public async Task The_passed_over_mail_is_asked_of_the_process_as_a_turn_once_its_node_takes_it_back()
    {
        await using var broker = await StartAsync();
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        var before = node.Commands.Count;

        await using var process = await PassedOverThenTakenBackAsync(broker, docs, node);

        _ = await node.CountCommandsAsync(before + 1, TimeSpan.FromMilliseconds(500));

        var after = node.Commands.Skip(before).ToList();

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, Assert.Single(after).CommandCase);
    }

    private static Task<LoopbackBroker> StartAsync() =>
        LoopbackBroker.StartAsync(Ct, options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always });

    // Attached again before its node re-announced it, so it reads as left running and a message's wake passes it over; then
    // the node re-announces it and it is the node's own.
    private static async Task<AttachedClient> PassedOverThenTakenBackAsync(LoopbackBroker broker, AttachedClient docs, JoinedNodeClient node)
    {
        var process = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: Token, processId: 4242);

        Assert.Equal(Wire.WakeOutcome.LeftRunning, Assert.Single((await docs.Client.RequestAsync(
            Requests.Open(Docs, Billing, "sent before the node is back", "c1"), cancellationToken: Ct)).Outcomes).Wake);

        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Billing, Token = Token }, cancellationToken: Ct);

        Assert.False(broker.Runtime.Unsupervised(AgentName.Create(Billing)));

        return process;
    }
}
