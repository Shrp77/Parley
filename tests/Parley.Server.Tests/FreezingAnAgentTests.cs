using Grpc.Core;
using Parley.Broker;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a person's freeze on an agent keeps it from being started by anything, and that unfreezing it undoes only that (SPEC.md §14).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is frozen is the choke point, not each caller.</b> The restart wake, new work and a person's Start all come to
/// one method, so these tests drive each of them and expect the node to hear nothing. A test of one would pass if a
/// second path were left open.
/// </para>
/// <para>
/// <b>Nothing is lost.</b> A freeze closes, moves and answers nothing, so every test that freezes an agent with work waiting
/// checks the work is still there afterwards.
/// </para>
/// </remarks>
[Integration]
public sealed class FreezingAnAgentTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static async Task RegisterAndCloseAsync(LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    private static Wire.AgentRequest Agent(string name) => new() { Agent = name };

    private static Wire.FreezeRequest Freeze(string name, bool keep = false) => new() { Agent = name, KeepFrozen = keep };

    [Fact]
    public async Task Work_for_a_frozen_agent_does_not_start_it_and_the_sender_is_told_why()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var frozen = await broker.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        Assert.True(frozen.Changed);
        Assert.Equal("not_running", frozen.Process);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);

        var sent = await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "orders@alpha", "while you are frozen", "c1"), cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.Frozen, Assert.Single(sent.Outcomes).Wake);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_persons_start_of_a_frozen_agent_is_refused_in_words()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();
        _ = await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.StartAsync(new Wire.StartRequest { Agent = "orders@alpha" }, cancellationToken: token));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("Unfreeze it first", refused.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Freezing_twice_says_it_changed_nothing_and_unfreezing_says_whether_it_was_frozen()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();

        Assert.True((await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token)).Changed);
        Assert.False((await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token)).Changed);
        Assert.True((await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);
        Assert.False((await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);
    }

    [Fact]
    public async Task Nothing_unregistered_and_neither_participant_the_broker_provides_can_be_frozen()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        var console = broker.ConnectAsOperator();

        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.FreezeAsync(Freeze("ghost@alpha"), cancellationToken: token));

        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);

        var seat = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.FreezeAsync(Freeze($"human-user@{DeadProject}"), cancellationToken: token));

        Assert.Equal(StatusCode.FailedPrecondition, seat.StatusCode);

        var facilitator = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.FreezeAsync(Freeze($"parley-facilitator@{DeadProject}"), cancellationToken: token));

        Assert.True(facilitator.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition);
    }

    private const string DeadProject = "alpha";

    [Fact]
    public async Task Unfreezing_an_agent_that_owes_work_starts_it_as_a_restart_would()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always });
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();
        _ = await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);
        _ = await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "orders@alpha", "waiting for you", "c1"), cancellationToken: token);

        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));

        _ = await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(500), Patience));
    }

    [Fact]
    public async Task Unfreezing_an_agent_that_collected_its_work_before_it_was_frozen_starts_it()
    {
        // The Request was collected, so it is work owed and no longer mail waiting: the half of the unfreeze's wake that asks
        // about open work is the only one that can see it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always });
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);

        await using (var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token))
        {
            var sent = Assert.Single((await docs.Client.RequestAsync(
                Requests.Open("docs@alpha", "orders@alpha", "collected, then frozen", "c1"), cancellationToken: token)).Outcomes);

            Assert.True(sent.Ok, sent.Rejection.ToString());

            // Collected and acknowledged, as an agent that read it and went away before answering.
            var collected = await orders.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: token);

            Assert.Single(collected.Messages);
        }

        await broker.WaitUntilDetachedAsync("orders@alpha", Patience);

        var console = broker.ConnectAsOperator();

        Assert.True((await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token)).Changed);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));

        Assert.True((await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(500), Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal("orders@alpha", command.Start.Agent);
    }

    [Fact]
    public async Task Unfreezing_an_agent_a_closure_poll_waits_on_for_nothing_but_its_vote_starts_it()
    {
        // The agent answered what it was asked and went away without voting, and was then frozen. It has no mail and owes no
        // work, so the poll is the only thing the unfreeze's wake can find; the broker's default policy wakes nobody for
        // open work, so nothing else could start it either.
        var token = TestContext.Current.CancellationToken;

        var clock = new StubClock(DateTimeOffset.UtcNow);
        await using var broker = await LoopbackBroker.StartAsync(token, clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);

        await using (var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token))
        {
            var sent = Assert.Single((await docs.Client.RequestAsync(
                Requests.Open("docs@alpha", "orders@alpha", "is it still read?", "c1"), cancellationToken: token)).Outcomes);

            Assert.True(sent.Ok, sent.Rejection.ToString());

            _ = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: token);

            // Proposed while the agent holds the work, so the agent is polled.
            var proposed = await docs.Client.ConcludeAsync(
                new Wire.ConcludeRequest { From = "docs@alpha", ConversationId = "c1", Vote = Wire.Vote.Done },
                cancellationToken: token);

            Assert.True(proposed.Ok);
            Assert.False(proposed.Concluded, "the conversation closed, so no poll waits on anybody");

            Assert.True((await orders.Client.ResultAsync(Requests.Result("orders@alpha", sent.ExchangeId), cancellationToken: token)).Ok);
        }

        // The Result asked for the silent voter once, while it was open outside Parley, and that asking holds the bound on
        // waking it until the ceiling passes. Moved past it, so the unfreeze below is asking about the poll and nothing else.
        clock.Advance(TimeSpan.FromMinutes(3));

        await broker.WaitUntilDetachedAsync("orders@alpha", Patience);

        var console = broker.ConnectAsOperator();

        Assert.True((await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token)).Changed);

        // It was open outside Parley until it went, and frozen since: nothing has been asked of the node.
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));

        Assert.True((await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(500), Patience));

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal("orders@alpha", command.Start.Agent);
    }

    [Fact]
    public async Task Mail_that_arrived_while_the_agent_was_open_outside_Parley_is_paid_by_an_unfreeze_soon_after()
    {
        // The agent was open in somebody's editor when the Request arrived, so no node was asked and nothing was coming. It
        // went away without collecting, and was frozen and unfrozen. The mail is still waiting, so the unfreeze owes it a
        // start, however soon after the Request it comes.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);

        await using (await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token))
        {
            var sent = Assert.Single((await docs.Client.RequestAsync(
                Requests.Open("docs@alpha", "orders@alpha", "sent while you were open outside", "c1"), cancellationToken: token)).Outcomes);

            Assert.Equal(Wire.WakeOutcome.OpenOutside, sent.Wake);
        }

        await broker.WaitUntilDetachedAsync("orders@alpha", Patience);

        var console = broker.ConnectAsOperator();

        Assert.True((await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token)).Changed);
        Assert.True((await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token)).Changed);

        Assert.Equal(1, await node.CountCommandsAsync(1, TimeSpan.FromMilliseconds(500), Patience));
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, (await node.WaitForCommandAsync(Patience)).CommandCase);
    }

    [Fact]
    public async Task Unfreezing_an_agent_that_was_not_frozen_starts_nobody_even_with_mail_waiting_for_it()
    {
        // The mail arrived before any node had joined, so nothing was asked for it. An unfreeze that lifted nothing is not a
        // reason to ask now: only a freeze that was lifted has a debt to pay.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);

        var sent = Assert.Single((await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "orders@alpha", "waiting, and you were never frozen", "c1"), cancellationToken: token)).Outcomes);

        Assert.True(sent.Ok, sent.Rejection.ToString());

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var unfrozen = await broker.ConnectAsOperator().UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.False(unfrozen.Changed);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Unfreezing_an_agent_that_owes_nothing_starts_nobody()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, options: BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always });
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();
        _ = await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);
        _ = await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_freeze_is_read_back_by_the_registry()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();

        Assert.Empty((await console.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows
            .Single(r => r.Name == "orders@alpha").FrozenUtc);

        _ = await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        Assert.NotEmpty((await console.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows
            .Single(r => r.Name == "orders@alpha").FrozenUtc);

        _ = await console.UnfreezeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.Empty((await console.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows
            .Single(r => r.Name == "orders@alpha").FrozenUtc);
    }

    [Fact]
    public async Task Freezing_closes_nothing_and_answers_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);
        _ = await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "orders@alpha", "still open after", "c1"), cancellationToken: token);

        var console = broker.ConnectAsOperator();
        _ = await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        var open = await console.ListConversationsAsync(new Wire.ListConversationsRequest(), cancellationToken: token);

        Assert.True(Assert.Single(open.Conversations).HasOpenExchange);
    }

    [Fact]
    public async Task A_frozen_agent_that_is_renamed_stays_frozen_under_its_new_name()
    {
        // The stored freeze goes with the row to the new name. The freeze the broker honours was left under the old one, so the
        // registry said frozen while new work and a person's start both started the agent (review, lens 2 run 1 on 0.64.3).
        var token = TestContext.Current.CancellationToken;
        var identity = Guid.NewGuid().ToString();

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing", "payments"], token);

        await using (await AttachedClient.AttachAsync(
            broker, "billing@alpha", workspace: "/work/billing", cancellationToken: token, identity: identity))
        {
        }

        await broker.WaitUntilDetachedAsync("billing@alpha", Patience);

        var console = broker.ConnectAsOperator();
        _ = await console.FreezeAsync(Freeze("billing@alpha", keep: true), cancellationToken: token);

        await using (await AttachedClient.AttachAsync(
            broker, "payments@alpha", workspace: "/work/billing", cancellationToken: token, identity: identity))
        {
        }

        await broker.WaitUntilDetachedAsync("payments@alpha", Patience);

        Assert.NotEmpty((await console.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows
            .Single(r => r.Name == "payments@alpha").FrozenUtc);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);

        var sent = await docs.Client.RequestAsync(
            Requests.Open("docs@alpha", "payments@alpha", "you were renamed while frozen", "c1"), cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.Frozen, Assert.Single(sent.Outcomes).Wake);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await console.StartAsync(new Wire.StartRequest { Agent = "payments@alpha" }, cancellationToken: token));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_name_whose_frozen_agent_was_purged_is_not_frozen_for_whoever_registers_under_it_next()
    {
        // A freeze is a column of the row. The purge took the row and left the name frozen in the broker's memory, so the next
        // agent under that name could not be started and no surface showed a freeze to lift (review, lens 2 run 1 on 0.64.3).
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        var console = broker.ConnectAsOperator();
        _ = await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);
        _ = await console.RetireAsync(Agent("orders@alpha"), cancellationToken: token);

        var purged = await console.PurgeAsync(Agent("orders@alpha"), cancellationToken: token);

        Assert.Equal(Wire.PurgeVerdict.Gone, purged.Verdict);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        Assert.True((await console.StartAsync(new Wire.StartRequest { Agent = "orders@alpha" }, cancellationToken: token)).Changed);
    }

    [Fact]
    public async Task Freezing_an_agent_open_outside_Parley_says_no_node_holds_its_process()
    {
        // SPEC.md §10.2: freeze returns what became of the agent's process, and for one somebody opened in an editor that is
        // "open outside Parley": the freeze stands, no node is asked, and the surfaces say its process stays until it is
        // closed there. Nothing held this arm: answered as "not running", every test passed.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var editor = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        var frozen = await broker.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        Assert.True(frozen.Changed);
        Assert.Equal("open_outside", frozen.Process);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Freezing_a_process_a_node_that_is_gone_left_says_so_and_blames_no_editor()
    {
        // A left process is nobody's editor, and only a person's kill ends it: answered "open_outside", the person was told to
        // wait for somebody to close it (review, lens 3 run 2 on 6fde4ef3).
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var left = await AttachedClient.AttachAsync(
            broker, "orders@alpha", cancellationToken: token, launchToken: "from-a-node-that-is-gone", processId: 4242);

        var frozen = await broker.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        Assert.True(frozen.Changed);
        Assert.Equal("left_running", frozen.Process);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Freezing_an_agent_whose_node_has_gone_says_it_was_left_running()
    {
        // The node that started it has left and its token went with it, so the process is left running (SPEC.md §11.1) and
        // only a person's kill ends it: the freeze says so. Answered as "not running", the person was told nothing of a
        // process still up; answered "no_node", the token was still read as held by a node that had gone (review, lens 1 run
        // C on 5de89187).
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var resident = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token, startedBy: node);

        await node.DisposeAsync();
        await broker.WaitUntilNodeGoneAsync("alpha", Patience);

        var frozen = await broker.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha"), cancellationToken: token);

        Assert.True(frozen.Changed);
        Assert.Equal("left_running", frozen.Process);
    }

    [Fact]
    public void An_agent_cannot_freeze_or_unfreeze_anybody()
    {
        // The Agents service is what an adapter binds. The operations are the person's alone (SPEC.md §10.2).
        var agentOperations = typeof(Wire.Agents.AgentsClient).GetMethods().Select(m => m.Name);

        Assert.DoesNotContain(agentOperations, name => name.StartsWith("Freeze", StringComparison.Ordinal));
        Assert.DoesNotContain(agentOperations, name => name.StartsWith("Unfreeze", StringComparison.Ordinal));
    }
}
