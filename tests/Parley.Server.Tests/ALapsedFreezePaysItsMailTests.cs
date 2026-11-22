using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a freeze the broker's next start lifted pays what it put off: an answer that arrived while its asker was frozen
/// gets the asker a turn when its machine's node joins, and a freeze the person kept pays nothing (SPEC.md §14).
/// </summary>
/// <remarks>
/// <para>
/// <b>A lapse is an unfreeze nobody was there to act on.</b> The broker lifts the freeze as it starts, before any node has
/// joined, so nothing can be started then. The asker owes nothing on its own question, so the wake a restart gives agents
/// that owe open work passes it by, under any resume policy: without the turn asked for here its answer waits until
/// something unrelated arrives.
/// </para>
/// <para>
/// <b>Run on a store file with the broker's default options</b>, whose resume policy is never, so the second broker is a
/// real restart over the same rows and the only thing that can start the agent is the lapse.
/// </para>
/// </remarks>
[Integration]
[Slow]
public sealed class ALapsedFreezePaysItsMailTests : IDisposable
{
    private const string Here = "alpha";
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(400);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-freeze-lapse").FullName;

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
            // A folder that cannot be removed is the temp directory's to clean.
        }
    }

    [Fact]
    public async Task An_answer_that_arrived_under_a_freeze_the_restart_lifted_gets_its_asker_one_start_when_its_node_joins()
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.False(await FrozenAsync(second), "the restart did not lift the freeze");

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

        // One, and no more: a start is a process.
        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);

        // Nobody unfroze it: the broker's start lifted the freeze, and the node's log says so (review, lens 1 run 2 on 6fde4ef3).
        Assert.Contains("freeze lapsed", command.Start.Because, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_attaches_again_inside_the_wait_is_not_started_a_second_time_by_the_lapse()
    {
        // The lapse's start waits as the restart wake does: an editor session that outlived the broker attaches again inside
        // the wait, and the agent is then open outside Parley, which nobody starts beside (SPEC.md §11.7, §14). Woken at the
        // join it was started 28 ms in, two sessions in one checkout (review, lens 3 run B on d99806d9).
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: TimeSpan.FromSeconds(2));

        Assert.False(await FrozenAsync(second), "the restart did not lift the freeze");

        // Not waiting for the wake at the join, which is the wait the editor attaches inside.
        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true, waitForRestartWake: false);
        await using var editor = await AttachedClient.AttachAsync(second, Orders, cancellationToken: Ct);

        await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        Assert.Equal(0, await node.CountCommandsAsync(Briefly));
    }

    [Fact]
    public async Task An_answer_that_arrived_under_a_freeze_the_person_kept_starts_nobody_when_the_node_joins()
    {
        await AnsweredWhileFrozenAsync(keep: true, answer: true);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.True(await FrozenAsync(second), "the restart lifted a freeze the person asked to keep");

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(0, await node.CountCommandsAsync(Briefly));
    }

    [Fact]
    public async Task A_lapsed_freeze_with_nothing_waiting_starts_nobody()
    {
        // The question is still open with the other agent, so nothing has arrived for the asker: there is nothing to pay.
        await AnsweredWhileFrozenAsync(keep: false, answer: false);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.False(await FrozenAsync(second));

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(0, await node.CountCommandsAsync(Briefly));
    }

    [Fact]
    public async Task Another_machines_node_joining_first_starts_nobody_and_leaves_the_start_for_the_agents_own_machine()
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        // It says it can launch the role, so only its machine's name keeps it from being taken for the agent's node.
        await using var elsewhere = await JoinedNodeClient.JoinAsync(second, "beta", ["orders"], Ct, reportsTurns: true);

        Assert.Equal(0, await elsewhere.CountCommandsAsync(Briefly));

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

        // Still owed after the other machine's join, and owed once.
        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);
        Assert.Equal(0, await elsewhere.CountCommandsAsync(Briefly));
    }

    [Fact]
    public async Task A_node_that_leaves_and_joins_again_is_not_asked_for_a_second_start()
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        await using (var first = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true))
        {
            Assert.Equal(1, await first.CountCommandsAsync(1, Briefly, Patience));
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, (await first.WaitForCommandAsync(Patience)).CommandCase);
        }

        await second.WaitUntilNodeGoneAsync(Here, Patience);

        // The process came up, began to collect and died before it acknowledged anything. The collection frees the bound on
        // asking for it again and the answer is still waiting, so the only thing between it and a second start at the next
        // join is that the lapse was used up by the first.
        await using (var orders = await AttachedClient.AttachAsync(second, Orders, cancellationToken: Ct))
        {
            using var call = orders.Client.DrainInbox(cancellationToken: Ct);

            await call.RequestStream.WriteAsync(
                new Wire.DrainInboxFrame { Collect = new Wire.DrainInboxRequest { Agent = Orders } }, Ct);

            Assert.True(await call.ResponseStream.MoveNext(Ct));
            Assert.NotEmpty(call.ResponseStream.Current.Messages);

            await call.RequestStream.CompleteAsync();

            Assert.False(await call.ResponseStream.MoveNext(Ct));
        }

        await second.WaitUntilDetachedAsync(Orders, Patience);

        await using var again = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(0, await again.CountCommandsAsync(Briefly));
    }

    [Fact]
    public async Task A_lapse_outlives_a_second_start_before_its_node_joins()
    {
        // Review, lens 1 run A on 6257cf80: the start cleared the stored freeze at once and kept what it owed only in memory, so
        // a second start before the node joined found nothing lapsed and the answer waited for something unrelated.
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            Assert.False(await FrozenAsync(second), "the restart did not lift the freeze");
        }

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct);
        await using var node = await JoinedNodeClient.JoinAsync(third, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);
    }

    [Fact]
    public async Task A_lapse_whose_broker_stopped_inside_the_wait_is_paid_once_by_the_next_start()
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: TimeSpan.FromSeconds(30)))
        {
            // Joined, and the broker stops while the lapse's start waits for sessions to attach again.
            await using var early = await JoinedNodeClient.JoinAsync(
                second, Here, ["orders"], Ct, reportsTurns: true, waitForRestartWake: false);

            Assert.Equal(0, await early.CountCommandsAsync(Briefly));
        }

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.False(await FrozenAsync(third), "the lapse read as a freeze");

        await using var node = await JoinedNodeClient.JoinAsync(third, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, (await node.WaitForCommandAsync(Patience)).CommandCase);
    }

    [Fact]
    public async Task A_lapse_paid_at_one_start_is_not_paid_again_at_the_next()
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

            Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));
        }

        // The answer is still uncollected (the start was asked of a node that started nothing), so only the lapse having been
        // cleared once paid stands between it and a second start here. The resume policy is never, so nothing else asks.
        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct);
        await using var again = await JoinedNodeClient.JoinAsync(third, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(0, await again.CountCommandsAsync(Briefly));
        Assert.False(await FrozenAsync(third));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_freeze_made_while_a_lapse_is_owed_takes_and_holds_across_a_restart_only_if_kept(bool keep)
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            var frozen = await second.ConnectAsOperator().FreezeAsync(
                new Wire.FreezeRequest { Agent = Orders, KeepFrozen = keep }, cancellationToken: Ct);

            Assert.True(frozen.Changed, "a freeze over a lapse was taken for one already made");
            Assert.True(await FrozenAsync(second));

            // The lapse's payment at the join starts nothing and does not clear the person's new freeze.
            await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

            Assert.Equal(0, await node.CountCommandsAsync(Briefly));
            Assert.True(await FrozenAsync(second), "the lapse's payment cleared the person's new freeze");
        }

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.Equal(keep, await FrozenAsync(third));

        await using var later = await JoinedNodeClient.JoinAsync(third, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(keep ? 0 : 1, await later.CountCommandsAsync(keep ? 0 : 1, Briefly, Patience));
    }

    [Fact]
    public async Task A_freeze_made_inside_the_lapses_wait_is_not_cleared_by_the_lapses_payment()
    {
        // The node has joined and the lapse's start is waiting for sessions to attach again when the person freezes the agent,
        // kept. The payment that follows finds it frozen, starts nothing, and must leave the person's freeze stored.
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using (var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: TimeSpan.FromSeconds(1)))
        {
            await using var node = await JoinedNodeClient.JoinAsync(
                second, Here, ["orders"], Ct, reportsTurns: true, waitForRestartWake: false);

            Assert.True((await second.ConnectAsOperator().FreezeAsync(
                new Wire.FreezeRequest { Agent = Orders, KeepFrozen = true }, cancellationToken: Ct)).Changed);

            await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

            Assert.Equal(0, await node.CountCommandsAsync(Briefly));
            Assert.True(await FrozenAsync(second), "the lapse's payment cleared the person's new freeze");
        }

        await using var third = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.True(await FrozenAsync(third), "the person's kept freeze did not outlast the restart");

        await using var later = await JoinedNodeClient.JoinAsync(third, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(0, await later.CountCommandsAsync(Briefly));
    }

    [Fact]
    public async Task An_unfreeze_of_a_lapse_still_owed_pays_it_once()
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: TimeSpan.FromSeconds(1));
        await using var node = await JoinedNodeClient.JoinAsync(
            second, Here, ["orders"], Ct, reportsTurns: true, waitForRestartWake: false);

        // Before the lapse's wait is over: the unfreeze pays, and the join that was to pay asks nothing more.
        var lifted = await second.ConnectAsOperator().UnfreezeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.True(lifted.Changed);

        await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);

        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));
        Assert.False(await FrozenAsync(second));
    }

    [Fact]
    public async Task The_registry_and_a_persons_start_read_a_lapse_still_owed_as_not_frozen()
    {
        await AnsweredWhileFrozenAsync(keep: false, answer: true);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        var row = (await second.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
            .Single(r => r.Name == Orders);

        Assert.Empty(row.FrozenUtc);
        Assert.False(row.KeepFrozen);
        Assert.False(second.Runtime.IsFrozen(Parley.Core.AgentName.Create(Orders)));

        // No node is here, so nothing starts, but it is not refused as frozen.
        var started = await second.ConnectAsOperator().StartAsync(new Wire.StartRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.False(started.Changed);
    }

    [Fact]
    public async Task A_lapse_owing_nothing_yet_is_kept_for_its_own_machine_when_another_machine_joins_first()
    {
        // Taken by another machine's wake, the lapse was paid with nothing owed and cleared, so the answer that arrived before
        // the agent's own node joined started nobody (review, lens 3 sweep A on 44c8e1e6).
        await AnsweredWhileFrozenAsync(keep: false, answer: false);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);
        await using var elsewhere = await JoinedNodeClient.JoinAsync(second, "beta", ["orders"], Ct, reportsTurns: true);

        await second.Runtime.RestartWakeOf("beta").WaitAsync(Patience, Ct);
        await AnswerAsync(second);

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);
        Assert.Equal(0, await elsewhere.CountCommandsAsync(Briefly));
    }

    [Fact]
    public async Task A_lapse_owing_nothing_yet_whose_node_leaves_inside_the_wait_is_kept_for_its_next_join()
    {
        // Taken with nobody to ask, the lapse was paid with nothing owed and cleared, so the answer that arrived while the node
        // was away started nobody when it came back (review, lens 3 sweep A on 44c8e1e6).
        await AnsweredWhileFrozenAsync(keep: false, answer: false);

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: TimeSpan.FromSeconds(1));

        await using (await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true, waitForRestartWake: false))
        {
        }

        await second.WaitUntilNodeGoneAsync(Here, Patience);
        await second.Runtime.RestartWakeOf(Here).WaitAsync(Patience, Ct);
        await AnswerAsync(second);

        await using var node = await JoinedNodeClient.JoinAsync(second, Here, ["orders"], Ct, reportsTurns: true);

        Assert.Equal(1, await node.CountCommandsAsync(1, Briefly, Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders, command.Start.Agent);
    }

    /// <summary>Billing answers the frozen agent's question on a later run, while no node of the asker's machine is joined.</summary>
    private static async Task AnswerAsync(LoopbackBroker broker)
    {
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var drained = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);
        var answered = await billing.Client.ResultAsync(
            Requests.Result(Billing, drained.Messages[0].Message.ExchangeId), cancellationToken: Ct);

        Assert.True(answered.Ok, "the answer was refused");
    }

    /// <summary>
    /// The first run of the broker: one agent asks another a question and is frozen, and the other answers or does not. No node
    /// is joined, so nothing is started and nothing is collected.
    /// </summary>
    private async Task AnsweredWhileFrozenAsync(bool keep, bool answer)
    {
        await using var first = await LoopbackBroker.StartOnAsync(Store, Ct);
        await using var billing = await AttachedClient.AttachAsync(first, Billing, cancellationToken: Ct);

        long exchange;

        await using (var orders = await AttachedClient.AttachAsync(first, Orders, cancellationToken: Ct))
        {
            var asked = Assert.Single((await orders.Client.RequestAsync(
                Requests.Open(Orders, Billing, "which ledger?", "c1"), cancellationToken: Ct)).Outcomes);

            Assert.True(asked.Ok, asked.Rejection.ToString());
            exchange = asked.ExchangeId;
        }

        await first.WaitUntilDetachedAsync(Orders, Patience);

        var frozen = await first.ConnectAsOperator().FreezeAsync(
            new Wire.FreezeRequest { Agent = Orders, KeepFrozen = keep }, cancellationToken: Ct);

        Assert.True(frozen.Changed);

        if (answer)
        {
            _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);

            var answered = await billing.Client.ResultAsync(Requests.Result(Billing, exchange), cancellationToken: Ct);

            Assert.True(answered.Ok, "the answer was refused, so nothing arrived while the asker was frozen");
        }
    }

    private static async Task<bool> FrozenAsync(LoopbackBroker broker) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
            .Single(r => r.Name == Orders).FrozenUtc.Length > 0;
}
