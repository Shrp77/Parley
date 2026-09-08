using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That an agent's answer to an instruction outlives the call that asked for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect, in one sentence: the broker settled an instruction because it had stopped
/// waiting.</b> Ceasing to wait is a fact about the broker; <c>failed</c> is a claim about the
/// turn — "attempted and did not complete" — which nobody is entitled to make while it is still
/// being attempted. And because settlement is first-come, writing it shut the record: when the
/// agent finished and the node reported what it had said, the report was refused. The work was
/// done, and both the operator and the store were told it had failed and that nothing was said.
/// </para>
/// <para>
/// <b>These run over a real socket because that is where the two halves meet.</b> The store's own
/// tests can pin first-come settlement and the sweep, and did — but the settling that should never
/// have happened was in the service, three layers away from the rule it defeated, and no test at
/// either end could see both.
/// </para>
/// </remarks>
public sealed class InstructionSurvivesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task An_answer_that_arrives_after_the_operators_call_has_gone_is_recorded()
    {
        // The whole feature. The operator gives up first; the agent answers second; the answer is
        // there afterwards, read back from a call that has nothing to do with the one that asked.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await RegisterAsync(broker, token);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        // Issued with a deadline far shorter than any real turn, which is exactly the shape of a
        // browser tab being closed: the caller stops listening while the agent works on.
        long instructionId;

        using (var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
        {
            var issuing = operators.InstructAsync(
                new Wire.InstructRequest
                {
                    Agent = "orders@alpha",
                    Text = "look at the retry policy"
                },
                cancellationToken: giveUp.Token);

            var command = await node.WaitForCommandAsync(Patience);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Instruct, command.CommandCase);

            instructionId = command.Instruct.InstructionId;

            // The operator's call dies here. Nothing about that is a fact about the turn.
            try
            {
                _ = await issuing;
            }
            catch (RpcException)
            {
                // Expected: the caller gave up. The point is what the broker did NOT write.
            }
        }

        // The agent finishes long after the operator stopped listening, and says what it did.
        _ = await node.Nodes.ReportInstructionAsync(
            new Wire.InstructionReport
            {
                Machine = "alpha",
                InstructionId = instructionId,
                Outcome = Wire.InstructionOutcome.Taken,
                Reply = "I changed three files and the tests pass"
            },
            cancellationToken: token);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = instructionId },
            cancellationToken: token);

        var instruction = Assert.Single(read.Instructions);

        Assert.Equal(Wire.InstructionOutcome.Taken, instruction.Outcome);
        Assert.Equal("I changed three files and the tests pass", instruction.Reply);

        // And the operator's own words came back with it, which is what makes the reply legible.
        Assert.Equal("look at the retry policy", instruction.Text);
    }

    [Fact]
    public async Task A_turn_still_running_reads_as_running_rather_than_failed()
    {
        // The distinction the feature turns on, asserted at the layer that used to get it wrong.
        // An operator acting on `failed` issues the instruction again -- which §10.2 names as the
        // one response that makes the situation worse.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await RegisterAsync(broker, token);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var issuing = operators.InstructAsync(
            new Wire.InstructRequest { Agent = "orders@alpha", Text = "a long one" },
            cancellationToken: giveUp.Token);

        var command = await node.WaitForCommandAsync(Patience);

        try
        {
            _ = await issuing;
        }
        catch (RpcException)
        {
        }

        // Nothing has reported. The record must say the turn is running, not that it failed.
        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = command.Instruct.InstructionId },
            cancellationToken: token);

        var instruction = Assert.Single(read.Instructions);

        Assert.Equal(Wire.InstructionOutcome.Pending, instruction.Outcome);
        Assert.Empty(instruction.Reply);
        Assert.Empty(instruction.SettledUtc);
    }

    [Fact]
    public async Task An_agent_on_an_unreachable_machine_settles_immediately_as_undeliverable()
    {
        // The one settlement the broker may still make on its own, and it is entitled to: no turn
        // was begun, so there is nothing to be premature about. §11.5 requires the operator to be
        // told rather than left looking as though the instruction arrived.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var agent = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await agent.DisposeAsync();

        var operators = broker.ConnectAsOperator();

        var said = await operators.InstructAsync(
            new Wire.InstructRequest { Agent = "docs@alpha", Text = "have a look" },
            cancellationToken: token);

        Assert.Equal(Wire.InstructionOutcome.Undeliverable, said.Outcome);
        Assert.True(said.InstructionId > 0, "the reply carried no identifier to read back by");

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = said.InstructionId },
            cancellationToken: token);

        Assert.Equal(
            Wire.InstructionOutcome.Undeliverable,
            Assert.Single(read.Instructions).Outcome);
    }

    [Fact]
    public async Task A_departing_node_settles_the_turns_it_was_holding()
    {
        // What replaced the broker settling on impatience. The machine that held the turn is gone,
        // so "attempted and did not complete" is exactly what happened -- claimed now by something
        // that knows rather than by something that merely stopped waiting.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await RegisterAsync(broker, token);

        var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var issuing = operators.InstructAsync(
            new Wire.InstructRequest { Agent = "orders@alpha", Text = "something slow" },
            cancellationToken: giveUp.Token);

        var command = await node.WaitForCommandAsync(Patience);

        try
        {
            _ = await issuing;
        }
        catch (RpcException)
        {
        }

        await node.DisposeAsync();
        await broker.WaitUntilNodeGoneAsync("alpha", Patience);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = command.Instruct.InstructionId },
            cancellationToken: token);

        Assert.Equal(Wire.InstructionOutcome.Failed, Assert.Single(read.Instructions).Outcome);
    }

    [Fact]
    public async Task An_agent_filter_that_is_not_a_name_is_refused_rather_than_ignored()
    {
        // Dropping an unparseable filter answers a narrow question with a wide result, and the
        // caller cannot tell: an empty list invites a second look, a full one reads as the answer.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().InstructionsAsync(
                new Wire.InstructionsRequest { Agent = "not a name" },
                cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
    }

    [Fact]
    public async Task The_broker_bounds_the_page_whatever_the_caller_asks_for()
    {
        // §10.2 makes rule 8.5's argument one level up: an unbounded operator query is the mistake
        // the message cap prevents, and a caller's number is a caller's number.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var read = await broker.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest { PageSize = int.MaxValue },
            cancellationToken: token);

        // Nothing to list, so the assertion is that asking for everything was survivable and
        // answered rather than refused or obeyed.
        Assert.Empty(read.Instructions);
        Assert.Empty(read.NextPageToken);
    }

    [Fact]
    public async Task An_operator_who_waits_out_the_brokers_patience_still_gets_the_answer()
    {
        // **THE defect, and the one every other test here misses.** The broker used to settle the
        // record `failed` the moment it stopped waiting, and settlement is first-come -- so the
        // agent's real answer, arriving minutes later, was refused. The work was done and the store
        // said it had failed and that nothing was said.
        //
        // The perverse part is why this case needs its own test: giving up EARLY always worked. The
        // settling write took the operator's own token, so a cancelled call cancelled the write, the
        // row stayed open, and the reply landed. Patience was the thing that was punished, and a
        // test that only ever cancels early exercises the half that was never broken.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, instructionPatience: TimeSpan.FromMilliseconds(250));

        await RegisterAsync(broker, token);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        // Waited out in full. No cancellation anywhere: this call returns of its own accord.
        var said = await operators.InstructAsync(
            new Wire.InstructRequest { Agent = "orders@alpha", Text = "something slow" },
            cancellationToken: token);

        // Still running, said in the one word that is true. Not `failed`, which claims the turn was
        // attempted and did not complete while it is still being attempted.
        Assert.Equal(Wire.InstructionOutcome.Pending, said.Outcome);
        Assert.True(said.InstructionId > 0, "nothing to come back to");

        var command = await node.WaitForCommandAsync(Patience);

        // The agent finishes well after the broker gave up, and says what it did.
        _ = await node.Nodes.ReportInstructionAsync(
            new Wire.InstructionReport
            {
                Machine = "alpha",
                InstructionId = command.Instruct.InstructionId,
                Outcome = Wire.InstructionOutcome.Taken,
                Reply = "I changed three files"
            },
            cancellationToken: token);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = said.InstructionId },
            cancellationToken: token);

        var instruction = Assert.Single(read.Instructions);

        Assert.Equal(Wire.InstructionOutcome.Taken, instruction.Outcome);
        Assert.Equal("I changed three files", instruction.Reply);
    }

    /// <summary>Puts the agent in the registry, which instructing it requires.</summary>
    /// <param name="broker">The broker.</param>
    /// <param name="token">Cancels the attach.</param>
    /// <returns>Nothing; the registration outlives the attachment.</returns>
    /// <remarks>
    /// <b>Attached and immediately released, and the two halves are for different things.</b> The
    /// instruction record carries a foreign key to the registry (§9.2), so an agent nothing has ever
    /// registered cannot be instructed at all — the write is refused before any delivery is tried.
    /// Being <i>attached</i> is not required and is deliberately not held: §11.5 gives delivery to
    /// the node, so what has to be up is the machine, not a session. A fixture that kept the
    /// attachment would be testing a stricter thing than the protocol asks for.
    /// </remarks>
    private static async Task RegisterAsync(LoopbackBroker broker, CancellationToken token)
    {
        var agent = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo-orders", token);

        await agent.DisposeAsync();
    }
}
