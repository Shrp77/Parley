using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a notification causes its recipient to run, and says what it caused.
/// </summary>
/// <remarks>
/// <para>
/// <b>This reverses SPEC.md §17 question 15</b>, which recorded the old behaviour as deliberate —
/// "a Notify nudges and never asks" — and warned that changing it would make turns unbounded. The
/// objection is answered by <c>WakeCoalescingTests</c> rather than by these: the wake is only
/// admissible because a hundred notifications cost one turn.
/// </para>
/// <para>
/// <b>Every fixture here joins a node, and that is not decoration.</b>
/// <c>BrokerRuntime.RequestAttention</c> returns at its first line when the recipient's machine has
/// no node — so a test without one passes having exercised the early return and asserted nothing
/// about the wake at all.
/// </para>
/// <para>
/// <b>Every fixture also registers the recipient first, and that is not decoration either.</b> §14
/// makes a notification to an agent nothing has ever registered <em>unroutable</em>, so the send is
/// refused before any of this is reached — which the first draft of these tests met as five
/// failures reading <c>Ok was False</c>. The wake therefore only ever applies to an agent that has
/// run at least once, which is the case that was reported: a catalogued role that had registered,
/// gone quiet, and could not be reached again.
/// </para>
/// </remarks>
public sealed class NotifyWakesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static Wire.NotifyRequest Notice(string text, string conversation = "c1") =>
        new()
        {
            From = "billing@alpha",
            To = "orders@alpha",
            ResponderModel = "test",
            PayloadJson = $$"""{"text":"{{text}}"}""",
            ConversationId = conversation
        };

    /// <summary>Registers an agent and leaves it closed, which is the state under test.</summary>
    /// <param name="broker">The broker.</param>
    /// <param name="name">The agent.</param>
    /// <param name="token">Cancels the attach.</param>
    /// <returns>A task that completes once the broker has seen it go.</returns>
    /// <remarks>
    /// Attach and release, rather than a registry write. §12.3 is emphatic that what registers an
    /// agent is the adapter it launches under its own launch identity, and a test that wrote the
    /// row itself would be exercising a path the deployment does not have.
    /// </remarks>
    private static async Task RegisterAndCloseAsync(
        LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    [Fact]
    public async Task A_notification_to_an_agent_nobody_has_open_asks_its_machine_to_start_it()
    {
        // The reported defect, in one test. Before this the message was written, collected by
        // nobody, and read only if somebody later opened the repository.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var reply = await sender.Client.NotifyAsync(
            Notice("the contract gained a field"), cancellationToken: token);

        Assert.True(reply.Ok);
        Assert.Equal(Wire.WakeOutcome.Started, reply.Wake);

        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal("orders@alpha", command.Start.Agent);
    }

    [Fact]
    public async Task Sixty_notifications_cost_one_turn_rather_than_sixty()
    {
        // **The count that matters is turns, not processes.** NodeCommand.StartAsync dispatches a
        // turn when the resident is already alive, so a node asked sixty times starts one process
        // and takes sixty turns. Counting processes would pass while the defect stood.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        for (var at = 0; at < 60; at++)
        {
            // Distinct, so §13.1's deduplication is not what is doing the work here. Sixty
            // identical notices would be coalesced by a rule this test is not about.
            var reply = await sender.Client.NotifyAsync(
                Notice($"notice {at}"), cancellationToken: token);

            Assert.True(reply.Ok);
        }

        Assert.Equal(1, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task The_sender_is_told_when_nothing_here_can_start_the_recipient()
    {
        // FR-013: not a failure. The message is stored and will be collected whenever that agent
        // next runs for any reason. Saying nothing is what produced the original report.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var reply = await sender.Client.NotifyAsync(
            Notice("nobody can start me"), cancellationToken: token);

        Assert.True(reply.Ok);
        Assert.Equal(Wire.WakeOutcome.Unreachable, reply.Wake);
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task A_deduplicated_notification_wakes_nobody()
    {
        // It carries nothing the recipient has not already been sent, so a turn spent on it is a
        // turn spent on nothing.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var first = await sender.Client.NotifyAsync(
            Notice("the same thing"), cancellationToken: token);

        Assert.False(first.Deduplicated);

        // Cleared, so the second is stopped by deduplication rather than by the coalescing bound —
        // which is what makes this a test of FR-003 and not a second copy of the one above.
        broker.Runtime.Collected(Parley.Core.AgentName.Create("orders@alpha"));

        var again = await sender.Client.NotifyAsync(
            Notice("the same thing"), cancellationToken: token);

        Assert.True(again.Deduplicated);
        Assert.Equal(Wire.WakeOutcome.AlreadyComing, again.Wake);

        Assert.Equal(1, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task A_notification_to_a_retired_agent_is_refused_and_starts_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        _ = await broker.ConnectAsOperator().RetireAsync(
            new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var reply = await sender.Client.NotifyAsync(
            Notice("you are gone"), cancellationToken: token);

        Assert.False(reply.Ok);
        Assert.Equal(0, await node.CountCommandsAsync(Settle));
    }

    [Fact]
    public async Task Draining_frees_the_next_notification_to_wake_again()
    {
        // The whole lifecycle over the wire: told, suppressed, collected, told again, asked again.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);
        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);
        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", cancellationToken: token);

        var first = await sender.Client.NotifyAsync(Notice("one"), cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.AlreadyRunning, first.Wake);

        var suppressed = await sender.Client.NotifyAsync(Notice("two"), cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.AlreadyComing, suppressed.Wake);

        // The real drain rather than the runtime call: that this handler clears the mark is the
        // property under test, and calling Collected here would be asserting on the fixture.
        _ = await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = "orders@alpha" }, cancellationToken: token);

        var after = await sender.Client.NotifyAsync(Notice("three"), cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.AlreadyRunning, after.Wake);
    }

    [Fact]
    public async Task A_notification_after_a_question_does_not_buy_a_second_turn()
    {
        // FR-008a, over the wire. "Ask a peer, then tell it something" is two messages and one
        // reading, and without this it is two turns charged to whoever hosts the peer.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var sender = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var asked = await sender.Client.AskAsync(
            new Wire.AskRequest
            {
                From = "billing@alpha",
                To = "orders@alpha",
                ResponderModel = "test",
                PayloadJson = """{"intent":"deciding whether to move the field","q1":"does anything read it?","consequences":"if nothing reads it I will drop it, and if something does I will deprecate it instead, so this decides the next commit."}""",
                ConversationId = "c1"
            },
            cancellationToken: token);

        Assert.True(asked.Ok);

        var told = await sender.Client.NotifyAsync(
            Notice("and one more thing"), cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.AlreadyComing, told.Wake);
        Assert.Equal(1, await node.CountCommandsAsync(Settle));
    }
}
