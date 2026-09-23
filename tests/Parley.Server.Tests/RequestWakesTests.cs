using System.Text.Json.Nodes;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That every message a participant is sent causes it to run, and that a hundred cost one turn.
/// </summary>
/// <remarks>
/// <para>
/// In 0.5 every message is answered (principle 2.7), so every Request, Clarify and Result asks for its recipient's
/// attention. A Request to an agent nobody has open is how a node learns to start it; a Result to a requester
/// whose session has ended is how the work it asked for gets picked back up.
/// </para>
/// <para>
/// <b>Every fixture here joins a node, and that is not decoration.</b> Asking for attention returns at its first
/// line when the recipient's machine has no node, so a test without one passes having exercised the early return.
/// </para>
/// <para>
/// <b>The bound counts turns, not processes.</b> A node asked sixty times starts one process and would take sixty
/// turns; the broker's coalescing is what makes a burst cost one, and it clears only when the recipient collects.
/// </para>
/// </remarks>
public sealed class RequestWakesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static Wire.OpenRequest Request(string text, string from = "billing@alpha", string to = "orders@alpha", string conversation = "c1") =>
        new()
        {
            From = from,
            To = to,
            ResponderModel = "test",
            PayloadJson = new JsonObject { ["ask"] = text }.ToJsonString(),
            ConversationId = conversation
        };

    /// <summary>Registers an agent and leaves it closed, which is the state under test.</summary>
    /// <remarks>
    /// Attach and release, rather than a registry write: what registers an agent is the adapter it launches under
    /// its own identity (§12.3), and a test that wrote the row itself would exercise a path deployments do not have.
    /// </remarks>
    private static async Task RegisterAndCloseAsync(LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    [Fact]
    public async Task A_request_to_an_agent_nobody_has_open_asks_its_machine_to_start_it()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var outcome = Assert.Single((await sender.Client.RequestAsync(Request("does anything read it?"), cancellationToken: token)).Outcomes);

        Assert.True(outcome.Ok);
        Assert.Equal(Wire.WakeOutcome.Started, outcome.Wake);

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal("orders@alpha", command.Start.Agent);
    }

    [Fact]
    public async Task Sixty_requests_cost_one_turn_rather_than_sixty()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        for (var at = 0; at < 60; at++)
        {
            // Distinct, so the distinctness rule is not what is doing the work here.
            Assert.True(Assert.Single((await sender.Client.RequestAsync(Request($"request {at}"), cancellationToken: token)).Outcomes).Ok);
        }

        Assert.Equal(1, await node.CountCommandsAsync(1, Settle));
    }

    [Fact]
    public async Task The_sender_is_told_when_nothing_here_can_start_the_receiver()
    {
        // Not a failure. The Request is stored and flagged, and collected whenever that agent next runs.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var outcome = Assert.Single((await sender.Client.RequestAsync(Request("nobody can start me"), cancellationToken: token)).Outcomes);

        Assert.True(outcome.Ok);
        Assert.Equal(Wire.WakeOutcome.Unreachable, outcome.Wake);
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task A_refused_request_wakes_nobody()
    {
        // The same Request still open is refused as a duplicate. It carries nothing the receiver has not already
        // been sent, so a turn spent on it is a turn spent on nothing.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        Assert.True(Assert.Single((await sender.Client.RequestAsync(Request("the same thing"), cancellationToken: token)).Outcomes).Ok);

        // Cleared, so the second is stopped by the refusal rather than by the coalescing bound.
        broker.Runtime.Collected(Parley.Core.AgentName.Create("orders@alpha"));

        var again = Assert.Single((await sender.Client.RequestAsync(Request("the same thing"), cancellationToken: token)).Outcomes);

        Assert.False(again.Ok);
        Assert.Equal(Wire.RequestRejection.Duplicate, again.Rejection);
        Assert.Equal(1, await node.CountCommandsAsync(1, Settle));
    }

    [Fact]
    public async Task A_request_to_a_retired_agent_is_refused_and_starts_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        _ = await broker.ConnectAsOperator().RetireAsync(new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        await using var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var outcome = Assert.Single((await sender.Client.RequestAsync(Request("you are gone"), cancellationToken: token)).Outcomes);

        Assert.False(outcome.Ok);
        Assert.Equal(Wire.RequestRejection.ReceiverRetired, outcome.Rejection);
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task Draining_frees_the_next_message_to_wake_again()
    {
        // The whole lifecycle over the wire: told, suppressed, collected, told again.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var sender = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.AlreadyRunning, Assert.Single((await sender.Client.RequestAsync(Request("one"), cancellationToken: token)).Outcomes).Wake);
        Assert.Equal(Wire.WakeOutcome.AlreadyComing, Assert.Single((await sender.Client.RequestAsync(Request("two"), cancellationToken: token)).Outcomes).Wake);

        // The real drain rather than the runtime call: that this handler clears the mark is the property under test.
        _ = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.AlreadyRunning, Assert.Single((await sender.Client.RequestAsync(Request("three"), cancellationToken: token)).Outcomes).Wake);
    }

    [Fact]
    public async Task A_result_to_a_requester_nobody_has_open_asks_its_machine_to_start_it()
    {
        // Every message is answered, so the Result is as much a reason to run as the Request was: the requester
        // asked for work because it needed it for something.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        // Orders sends its Request and its session ends, which is the case under test: the work it asked for comes
        // back to nobody running.
        Wire.RequestOutcome sent;

        await using (var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token))
        {
            sent = Assert.Single((await orders.Client.RequestAsync(Request("does anything read it?", from: "orders@alpha", to: "billing@alpha"), cancellationToken: token)).Outcomes);
            Assert.True(sent.Ok);
        }

        await broker.WaitUntilDetachedAsync("orders@alpha", Patience);

        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: token);

        var result = await billing.Client.ResultAsync(
            new Wire.ResultRequest
            {
                From = "billing@alpha",
                ExchangeId = sent.ExchangeId,
                ResponderModel = "test",
                PayloadJson = """{"outcomes":[{"id":"request","outcome":"answered","text":"nothing reads it"}]}"""
            },
            cancellationToken: token);

        Assert.True(result.Ok);

        var command = await node.WaitForCommandAsync(Patience);
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal("orders@alpha", command.Start.Agent);
    }

    [Fact]
    public async Task A_participant_a_poll_waits_on_for_nothing_but_its_vote_is_given_a_turn()
    {
        // Found by the review gate. Orders proposed closure while billing held the work, so both were polled; billing
        // then sent its Result and never voted. The poll reaches billing only through its inbox, and billing had no
        // reason to collect again, so nothing asked it (§5.0). The node offers billing and not orders, so the turn
        // below can only be the silent voter's.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        var sent = Assert.Single((await orders.Client.RequestAsync(Request("is it still read?", from: "orders@alpha", to: "billing@alpha"), cancellationToken: token)).Outcomes);
        Assert.True(sent.Ok);
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(Patience)).CommandCase);

        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: token);

        var proposed = await orders.Client.ConcludeAsync(
            new Wire.ConcludeRequest { From = "orders@alpha", ConversationId = "c1", Vote = Wire.Vote.Done },
            cancellationToken: token);

        Assert.True(proposed.Ok);
        Assert.False(proposed.Concluded);

        var result = await billing.Client.ResultAsync(
            new Wire.ResultRequest
            {
                From = "billing@alpha",
                ExchangeId = sent.ExchangeId,
                ResponderModel = "test",
                PayloadJson = """{"outcomes":[{"id":"request","outcome":"answered","text":"nothing reads it"}]}"""
            },
            cancellationToken: token);

        Assert.True(result.Ok);
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(Patience)).CommandCase);
    }

    [Fact]
    public async Task A_decline_turning_into_agreement_asks_the_silent_once_and_repeating_it_buys_nothing()
    {
        // Found by the review gate, in the fix for the test above. The first version woke the silent voters after
        // every vote that did not close the conversation, and five repeated agreements bought the silent voter five
        // turns: each turn is a collection, and a collection frees the next wake. Only inventory is offered by the
        // node, so every command below is inventory's.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["inventory"], token);
        await using var inventory = await AttachedClient.AttachAsync(broker, "inventory@alpha", cancellationToken: token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        var toBilling = Assert.Single((await orders.Client.RequestAsync(Request("gross or net?", from: "orders@alpha", to: "billing@alpha"), cancellationToken: token)).Outcomes);
        var toInventory = Assert.Single((await orders.Client.RequestAsync(Request("in stock?", from: "orders@alpha", to: "inventory@alpha"), cancellationToken: token)).Outcomes);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(Patience)).CommandCase);

        _ = await inventory.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "inventory@alpha" }, cancellationToken: token);
        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "billing@alpha" }, cancellationToken: token);

        Assert.True((await orders.Client.ConcludeAsync(Vote("orders@alpha", Wire.Vote.Done), cancellationToken: token)).Ok);
        Assert.True((await inventory.Client.ResultAsync(Answer("inventory@alpha", toInventory.ExchangeId), cancellationToken: token)).Ok);
        Assert.True((await billing.Client.ConcludeAsync(Vote("billing@alpha", Wire.Vote.NotDone), cancellationToken: token)).Ok);
        Assert.True((await billing.Client.ResultAsync(Answer("billing@alpha", toBilling.ExchangeId), cancellationToken: token)).Ok);

        // Nothing is open, but billing's decline holds it: nobody is asked.
        await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Settle));

        var agreed = await billing.Client.ConcludeAsync(Vote("billing@alpha", Wire.Vote.Done), cancellationToken: token);

        Assert.False(agreed.Concluded);
        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(Patience)).CommandCase);

        for (var round = 0; round < 3; round++)
        {
            _ = await inventory.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "inventory@alpha" }, cancellationToken: token);
            _ = await orders.Client.ConcludeAsync(Vote("orders@alpha", Wire.Vote.Done), cancellationToken: token);
            _ = await billing.Client.ConcludeAsync(Vote("billing@alpha", Wire.Vote.Done), cancellationToken: token);

            await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Settle));
        }

        // And the replay lens 2 found in the rule this test was first written for: waking on a decline turning into
        // agreement let billing buy inventory a turn for every two calls, by alternating its vote.
        for (var round = 0; round < 3; round++)
        {
            _ = await inventory.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = "inventory@alpha" }, cancellationToken: token);
            _ = await billing.Client.ConcludeAsync(Vote("billing@alpha", Wire.Vote.NotDone), cancellationToken: token);
            _ = await billing.Client.ConcludeAsync(Vote("billing@alpha", Wire.Vote.Done), cancellationToken: token);

            await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(Settle));
        }

        static Wire.ConcludeRequest Vote(string from, Wire.Vote vote) =>
            new() { From = from, ConversationId = "c1", Vote = vote };

        static Wire.ResultRequest Answer(string from, long exchangeId) =>
            new()
            {
                From = from,
                ExchangeId = exchangeId,
                ResponderModel = "test",
                PayloadJson = """{"outcomes":[{"id":"request","outcome":"answered","text":"yes"}]}"""
            };
    }
}
