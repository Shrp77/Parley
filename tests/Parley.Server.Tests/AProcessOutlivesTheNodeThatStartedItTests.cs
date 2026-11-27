using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a process whose node has gone reads as left running while the broker stays up, and as supervised again once a node
/// that rejoins announces it (SPEC.md §11.1: an attachment that presents a token no node now holds is left running).
/// </summary>
/// <remarks>
/// Review, lens 1 run C on 5de89187, with real processes: a node's release dropped its turns and background work but kept
/// the tokens it had reported, so every process it started still read as one a node supervises. With the node hard-killed
/// and replaced, senders were told a turn was coming that the new node dropped, a person's kill was refused as "one a node
/// supervises is stopped, not killed", and the stop it pointed to reached a node that was not running it.
/// </remarks>
[Integration]
public sealed class AProcessOutlivesTheNodeThatStartedItTests
{
    private const string Here = "m1";
    private const string Billing = "billing@m1";
    private const string Docs = "docs@m1";
    private const string Token = "started-by-the-first-node";
    private const long Process = 4242;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Once_its_node_has_gone_it_reads_as_left_running_its_wake_is_passed_over_and_a_person_may_kill_it()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await using var process = await StartedThenOrphanedAsync(broker);

        // A replacement node holds the machine now and knows nothing of the process.
        await using var replacement = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        Assert.True(broker.Runtime.Unsupervised(AgentName.Create(Billing)), "still left running once a new node has joined");

        var sent = Assert.Single((await docs.Client.RequestAsync(
            Requests.Open(Docs, Billing, "sent while no node holds it", "c1"), cancellationToken: Ct)).Outcomes);

        Assert.True(sent.Ok);
        Assert.Equal(Wire.WakeOutcome.LeftRunning, sent.Wake);

        var reply = await broker.ConnectAsOperator().KillAsync(
            new Wire.StopRequest { Agent = Billing, Because = "its node is gone" }, cancellationToken: Ct);

        Assert.True(reply.Changed);

        var command = await replacement.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Kill, command.CommandCase);
        Assert.Equal(Billing, command.Kill.Agent);
        Assert.Equal(Process, command.Kill.ProcessId);
    }

    [Fact]
    public async Task A_node_that_rejoins_and_announces_it_again_supervises_it_and_starts_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, Docs, cancellationToken: Ct);
        await using var process = await StartedThenOrphanedAsync(broker);

        // The same node, back on a new connection: it announces what it still runs (TurnReporter.ReannounceAsync).
        await using var rejoined = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        var before = rejoined.Commands.Count;

        // Sent in the gap, before the announcement, so its wake is passed over to whoever takes the process.
        Assert.Equal(Wire.WakeOutcome.LeftRunning, Assert.Single((await docs.Client.RequestAsync(
            Requests.Open(Docs, Billing, "sent before the node announces it", "c1"), cancellationToken: Ct)).Outcomes).Wake);

        _ = await rejoined.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Billing, Token = Token }, cancellationToken: Ct);

        Assert.False(broker.Runtime.Unsupervised(AgentName.Create(Billing)));
        Assert.False(broker.Runtime.OpenOutside(AgentName.Create(Billing)));

        // The passed-over mail is asked of the running process as a turn: never a second process beside it.
        _ = await rejoined.CountCommandsAsync(before + 1, TimeSpan.FromMilliseconds(500), Patience);

        var after = rejoined.Commands.Skip(before).Select(command => command.CommandCase).ToList();

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, Assert.Single(after));
    }

    // A node starts billing, its process attaches with the token, and the node goes while the process stays: returns once the
    // broker has finished letting the node go.
    private static async Task<AttachedClient> StartedThenOrphanedAsync(LoopbackBroker broker)
    {
        AttachedClient process;

        await using (var first = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true))
        {
            _ = await first.Nodes.ReportLaunchAsync(
                new Wire.LaunchReport { Machine = Here, Agent = Billing, Token = Token }, cancellationToken: Ct);

            process = await AttachedClient.AttachAsync(
                broker, Billing, cancellationToken: Ct, launchToken: Token, processId: Process);

            Assert.False(broker.Runtime.Unsupervised(AgentName.Create(Billing)), "supervised while its node is here");
        }

        await broker.WaitUntilNodeGoneAsync(Here, Patience);

        // The node leaves the list before its release has finished, so the release is waited for by what it does.
        var deadline = DateTime.UtcNow + Patience;

        while (!broker.Runtime.Unsupervised(AgentName.Create(Billing)))
        {
            Assert.True(DateTime.UtcNow < deadline, "the process still reads as supervised after its node has gone");
            await Task.Delay(20, Ct);
        }

        Assert.True(process.StillAttached);

        return process;
    }
}
