using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What a node may write into the operator's own record, and what it may not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reporting path is the one place something other than the operator writes into the
/// instruction log.</b> §16 is explicit that anything able to reach the broker can claim to be any
/// agent, so "the node is trusted" is a statement about the network rather than about the caller —
/// and the readback made this reachable in a way it was not before, because a reply now arrives in
/// a listing rather than only as the return value of the call that asked for it.
/// </para>
/// <para>
/// These probe the report as an attacker would use it: a reply nobody bounded, an identifier that
/// belongs to somebody else, and one that belongs to nothing at all.
/// </para>
/// </remarks>
public sealed class InstructionReportGuardTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static async Task<(LoopbackBroker Broker, JoinedNodeClient Node, long Id)> PendingAsync(
        CancellationToken token)
    {
        var broker = await LoopbackBroker.StartAsync(token);

        var agent = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await agent.DisposeAsync();

        var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var issuing = broker.ConnectAsOperator().InstructAsync(
            new Wire.InstructRequest { Agent = "orders@alpha", Text = "have a look" },
            cancellationToken: giveUp.Token);

        var command = await node.WaitForCommandAsync(Patience);

        try
        {
            _ = await issuing;
        }
        catch (RpcException)
        {
        }

        return (broker, node, command.Instruct.InstructionId);
    }

    [Fact]
    public async Task A_reply_far_over_the_message_cap_does_not_land_unbounded_in_the_store()
    {
        // **Rule 8.5's argument, one level out, and this probe found it missing.** The instruction's
        // own text has been capped by the schema for two migrations; the reply column carried no
        // CHECK and the reporting path applied none, so a node -- or anything able to reach the
        // broker and claim to be one, which §16 says is anything on the network -- could write an
        // arbitrarily large blob into the operator's store. Two hundred thousand characters were
        // stored and read back whole. Both halves are bounded now: the edge fits, the schema
        // refuses.
        var token = TestContext.Current.CancellationToken;
        var (broker, node, id) = await PendingAsync(token);

        await using (broker)
        await using (node)
        {
            var enormous = new string('x', 200_000);

            _ = await node.Nodes.ReportInstructionAsync(
                new Wire.InstructionReport
                {
                    Machine = "alpha",
                    InstructionId = id,
                    Outcome = Wire.InstructionOutcome.Taken,
                    Reply = enormous
                },
                cancellationToken: token);

            var read = await broker.ConnectAsOperator().InstructionsAsync(
                new Wire.InstructionsRequest { InstructionId = id },
                cancellationToken: token);

            var stored = Assert.Single(read.Instructions).Reply;

            // Bounded, and the cut says so rather than passing for the agent's own ending.
            Assert.True(
                stored.Length <= 8000,
                $"a reply of {stored.Length} characters was stored, against a cap of 8000 on the "
                + "instruction beside it");

            Assert.EndsWith(
                Parley.Core.MessageLimits.Truncated, stored, StringComparison.Ordinal);

            // And what did fit is the agent's, unaltered.
            Assert.StartsWith("xxx", stored, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_report_for_an_instruction_nobody_issued_changes_nothing()
    {
        // An identifier is not an authorisation. A report naming a row that does not exist must be
        // refused quietly rather than creating one -- a row invented here would carry an agent name
        // nothing registered and a reply nobody asked for.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        _ = await node.Nodes.ReportInstructionAsync(
            new Wire.InstructionReport
            {
                Machine = "alpha",
                InstructionId = 999_999,
                Outcome = Wire.InstructionOutcome.Taken,
                Reply = "an answer to a question nobody asked"
            },
            cancellationToken: token);

        var read = await broker.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest(),
            cancellationToken: token);

        Assert.Empty(read.Instructions);
    }

    [Fact]
    public async Task A_second_report_cannot_rewrite_an_answer_the_operator_has_already_read()
    {
        // First-come settlement as a security property rather than a correctness one. If a later
        // report could overwrite, anything on the network could change what an agent is recorded as
        // having said, after somebody had acted on it.
        var token = TestContext.Current.CancellationToken;
        var (broker, node, id) = await PendingAsync(token);

        await using (broker)
        await using (node)
        {
            _ = await node.Nodes.ReportInstructionAsync(
                new Wire.InstructionReport
                {
                    Machine = "alpha",
                    InstructionId = id,
                    Outcome = Wire.InstructionOutcome.Taken,
                    Reply = "the truthful answer"
                },
                cancellationToken: token);

            _ = await node.Nodes.ReportInstructionAsync(
                new Wire.InstructionReport
                {
                    Machine = "alpha",
                    InstructionId = id,
                    Outcome = Wire.InstructionOutcome.Failed,
                    Reply = "a later lie"
                },
                cancellationToken: token);

            var read = await broker.ConnectAsOperator().InstructionsAsync(
                new Wire.InstructionsRequest { InstructionId = id },
                cancellationToken: token);

            var instruction = Assert.Single(read.Instructions);

            Assert.Equal(Wire.InstructionOutcome.Taken, instruction.Outcome);
            Assert.Equal("the truthful answer", instruction.Reply);
        }
    }

    [Fact]
    public async Task An_over_long_instruction_is_refused_before_it_is_recorded()
    {
        // The other half of the cap, and the one that already worked. Checked here so the pair is
        // visible together: an operator's words are bounded and an agent's are the open question.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().InstructAsync(
                new Wire.InstructRequest
                {
                    Agent = "orders@alpha",
                    Text = new string('x', 8001)
                },
                cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);

        var read = await broker.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest(),
            cancellationToken: token);

        Assert.Empty(read.Instructions);
    }
}
