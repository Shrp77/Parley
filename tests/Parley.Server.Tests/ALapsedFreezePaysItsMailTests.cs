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
