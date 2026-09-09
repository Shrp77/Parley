using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Many instructions in flight at once, and what happens to the ones nobody is waiting on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Giving up no longer settles anything, which changed who is waiting for what.</b> The broker
/// keeps a waiter per in-flight instruction and removes it when its call returns — so once a call
/// returns *still running*, the report that arrives afterwards has nothing to wake. That path is
/// now the ordinary one rather than the exception, and it had never been driven at volume.
/// </para>
/// <para>
/// What is probed here is that the two halves stay matched under load: every instruction settles
/// exactly once, with its own answer and not a neighbour's, whether or not anybody was still
/// listening when it finished.
/// </para>
/// </remarks>
public sealed class InstructionsInFlightTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Twenty_turns_settle_with_their_own_answers_and_not_a_neighbours()
    {
        // The failure this would catch is an identifier mixed up between waiter and report, which
        // would show as an operator reading somebody else's reply under their own instruction --
        // silently, and only detectable by having asked twenty different questions at once.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, instructionPatience: TimeSpan.FromMilliseconds(150));

        var agent = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await agent.DisposeAsync();

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        var issued = new List<(long Id, string Text)>();

        for (var at = 0; at < 20; at++)
        {
            var said = await operators.InstructAsync(
                new Wire.InstructRequest { Agent = "orders@alpha", Text = $"question {at}" },
                cancellationToken: token);

            // Every one of these outlives the broker's patience, so every one comes back running.
            Assert.Equal(Wire.InstructionOutcome.Pending, said.Outcome);

            issued.Add((said.InstructionId, $"question {at}"));
        }

        // Every identifier is distinct, or the rest of this proves nothing.
        Assert.Equal(issued.Count, issued.Select(i => i.Id).Distinct().Count());

        // Answered out of order, because nothing says a node finishes turns in the order it got
        // them and an implementation that assumed so would pass a test that fed them back in order.
        foreach (var (id, text) in issued.AsEnumerable().Reverse())
        {
            _ = await node.Nodes.ReportInstructionAsync(
                new Wire.InstructionReport
                {
                    Machine = "alpha",
                    InstructionId = id,
                    Outcome = Wire.InstructionOutcome.Taken,
                    Reply = "answer to " + text
                },
                cancellationToken: token);
        }

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { Agent = "orders@alpha", PageSize = 100 },
            cancellationToken: token);

        Assert.Equal(issued.Count, read.Instructions.Count);

        foreach (var instruction in read.Instructions)
        {
            // The pairing, which is the whole assertion: this instruction's reply names this
            // instruction's question.
            Assert.Equal("answer to " + instruction.Text, instruction.Reply);
            Assert.Equal(Wire.InstructionOutcome.Taken, instruction.Outcome);
        }
    }

    [Fact]
    public async Task Instructions_issued_at_once_all_reach_the_node_and_all_are_recorded()
    {
        // Concurrent callers rather than sequential ones. Two operators, or one console and one
        // terminal, instructing the same agent at the same moment is ordinary -- and the record has
        // to hold both, since the alternative is a person's words vanishing because somebody else
        // typed at the same time.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, instructionPatience: TimeSpan.FromMilliseconds(150));

        var agent = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await agent.DisposeAsync();

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        var issuing = Enumerable.Range(0, 12).Select(async at =>
        {
            var said = await operators.InstructAsync(
                new Wire.InstructRequest { Agent = "orders@alpha", Text = $"at once {at}" },
                cancellationToken: token);

            return said.InstructionId;
        });

        var ids = await Task.WhenAll(issuing);

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(0, ids);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { Agent = "orders@alpha", PageSize = 100 },
            cancellationToken: token);

        Assert.Equal(12, read.Instructions.Count);

        // Each is still running: nothing reported, and nothing settled them on the way out.
        Assert.All(
            read.Instructions,
            i => Assert.Equal(Wire.InstructionOutcome.Pending, i.Outcome));
    }

    [Fact]
    public async Task A_report_arriving_after_everyone_stopped_waiting_still_lands()
    {
        // The path that used to be an exception and is now the normal one. Nothing is awaiting the
        // identifier by the time the node answers, so the report has only the store to write to --
        // and if it wrote only to the waiter, every slow turn would answer into nothing.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, instructionPatience: TimeSpan.FromMilliseconds(120));

        var agent = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await agent.DisposeAsync();

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        var said = await operators.InstructAsync(
            new Wire.InstructRequest { Agent = "orders@alpha", Text = "slow" },
            cancellationToken: token);

        Assert.Equal(Wire.InstructionOutcome.Pending, said.Outcome);

        var command = await node.WaitForCommandAsync(Patience);

        _ = await node.Nodes.ReportInstructionAsync(
            new Wire.InstructionReport
            {
                Machine = "alpha",
                InstructionId = command.Instruct.InstructionId,
                Outcome = Wire.InstructionOutcome.Declined,
                Reply = "not my repository"
            },
            cancellationToken: token);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = said.InstructionId },
            cancellationToken: token);

        var instruction = Assert.Single(read.Instructions);

        // A decline, kept as a decline. §10.2 requires it told apart from a failure, and this is
        // the path where the distinction would be easiest to lose.
        Assert.Equal(Wire.InstructionOutcome.Declined, instruction.Outcome);
        Assert.Equal("not my repository", instruction.Reply);
    }

    [Fact]
    public async Task Paging_over_the_wire_returns_every_instruction_once()
    {
        // The broker bounds the page and hands back a token; a caller that walks it must see each
        // row exactly once. A cursor that repeated would loop, and one that skipped would hide an
        // instruction an operator gave.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, instructionPatience: TimeSpan.FromMilliseconds(100));

        var agent = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await agent.DisposeAsync();

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var operators = broker.ConnectAsOperator();

        for (var at = 0; at < 7; at++)
        {
            _ = await operators.InstructAsync(
                new Wire.InstructRequest { Agent = "orders@alpha", Text = $"paged {at}" },
                cancellationToken: token);
        }

        var seen = new List<long>();
        var pageToken = string.Empty;

        for (var page = 0; page < 10; page++)
        {
            var read = await operators.InstructionsAsync(
                new Wire.InstructionsRequest { PageSize = 3, PageToken = pageToken },
                cancellationToken: token);

            seen.AddRange(read.Instructions.Select(i => i.Id));

            // Bounded whatever was asked, and never over the size requested.
            Assert.True(read.Instructions.Count <= 3);

            if (string.IsNullOrEmpty(read.NextPageToken))
            {
                break;
            }

            pageToken = read.NextPageToken;
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }
}
