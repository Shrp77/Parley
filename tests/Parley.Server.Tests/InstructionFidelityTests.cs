using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What survives the round trip, and what an identifier does and does not identify.
/// </summary>
/// <remarks>
/// <para>
/// <b>An instruction crosses four representations to be read back.</b> A row in SQLite, a protobuf
/// message, a domain record, and whatever a surface prints. Every one of those hops converts the
/// two instants through a string, and the readback exists precisely so somebody can ask *when*
/// long after the fact — a timestamp that lost its offset would be wrong by hours and look fine.
/// </para>
/// <para>
/// The identifier half is a different worry. Instructions are numbered per broker, and a broker is
/// one project (§4.1), so identifier 5 exists in every project and means something different in
/// each. A surface that carried one between projects would show an operator somebody else's
/// instruction under their own agent's name.
/// </para>
/// </remarks>
public sealed class InstructionFidelityTests
{
    private static async Task<(long Id, LoopbackBroker Broker)> ToldAsync(
        LoopbackBroker broker, string text, CancellationToken token)
    {
        var agent = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await agent.DisposeAsync();

        var said = await broker.ConnectAsOperator().InstructAsync(
            new Wire.InstructRequest { Agent = "orders@alpha", Text = text },
            cancellationToken: token);

        return (said.InstructionId, broker);
    }

    [Fact]
    public async Task The_instant_an_instruction_was_issued_survives_the_round_trip()
    {
        // Store to wire to client and back. The failure this catches is a conversion that drops the
        // offset or the sub-second part: an operator reading "09:00" for something issued at 09:00
        // UTC from a machine an hour ahead has been told the wrong thing about their own action,
        // and nothing anywhere would report it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var (id, _) = await ToldAsync(broker, "when was this", token);

        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var read = await broker.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = id },
            cancellationToken: token);

        var instruction = Assert.Single(read.Instructions);

        Assert.False(string.IsNullOrEmpty(instruction.IssuedUtc), "no instant came back at all");

        var issued = DateTimeOffset.Parse(
            instruction.IssuedUtc, System.Globalization.CultureInfo.InvariantCulture);

        // Inside the window this test spans. A dropped offset would put it hours outside.
        Assert.InRange(issued, before, after);
    }

    [Fact]
    public async Task A_settled_instant_comes_back_and_an_unsettled_one_does_not()
    {
        // The pairing the schema enforces, checked on the far side of four conversions. A settled
        // instant arriving on a running instruction would be a state the store refuses, assembled
        // by the transport.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, instructionPatience: TimeSpan.FromMilliseconds(120));

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var (id, _) = await ToldAsync(broker, "slow one", token);

        var operators = broker.ConnectAsOperator();

        var running = Assert.Single((await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = id },
            cancellationToken: token)).Instructions);

        Assert.Equal(Wire.InstructionOutcome.Pending, running.Outcome);
        Assert.Empty(running.SettledUtc);

        _ = await node.Nodes.ReportInstructionAsync(
            new Wire.InstructionReport
            {
                Machine = "alpha",
                InstructionId = id,
                Outcome = Wire.InstructionOutcome.Taken,
                Reply = "done"
            },
            cancellationToken: token);

        var settled = Assert.Single((await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = id },
            cancellationToken: token)).Instructions);

        Assert.NotEmpty(settled.SettledUtc);

        var at = DateTimeOffset.Parse(
            settled.SettledUtc, System.Globalization.CultureInfo.InvariantCulture);
        var issued = DateTimeOffset.Parse(
            settled.IssuedUtc, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(at >= issued, "an instruction settled before it was issued");
    }

    [Fact]
    public async Task Text_and_reply_cross_unaltered_including_what_would_break_a_parser()
    {
        // Four representations, two of them textual. Quotes, braces, backslashes and non-ASCII are
        // where a conversion that concatenated rather than encoded would show itself -- and an
        // operator's words arriving subtly different from what they typed is the quiet kind of
        // wrong.
        const string awkward = "look at {\"json\": \"in the text\"} and C:\\paths\\here — plus ünïcode";

        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(
            token, instructionPatience: TimeSpan.FromMilliseconds(120));

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        var (id, _) = await ToldAsync(broker, awkward, token);

        _ = await node.Nodes.ReportInstructionAsync(
            new Wire.InstructionReport
            {
                Machine = "alpha",
                InstructionId = id,
                Outcome = Wire.InstructionOutcome.Taken,
                Reply = awkward
            },
            cancellationToken: token);

        var read = Assert.Single((await broker.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = id },
            cancellationToken: token)).Instructions);

        Assert.Equal(awkward, read.Text);
        Assert.Equal(awkward, read.Reply);
    }

    [Fact]
    public async Task An_identifier_means_nothing_in_another_projects_broker()
    {
        // **Identifiers are per broker and a broker is one project.** Identifier 1 exists in every
        // deployment, so a surface that carried one from one project to another would show an
        // operator a stranger's instruction under a name they recognise. Neither surface does --
        // both read the project they are showing -- and this is what would catch it if one started.
        var token = TestContext.Current.CancellationToken;

        await using var alpha = await LoopbackBroker.StartAsync(token);
        await using var beta = await LoopbackBroker.StartAsync(token);

        var (mine, _) = await ToldAsync(alpha, "alpha's own instruction", token);
        var (theirs, _) = await ToldAsync(beta, "beta's own instruction", token);

        // The point of the fixture: the same number in both stores.
        Assert.Equal(mine, theirs);

        var fromAlpha = Assert.Single((await alpha.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = mine },
            cancellationToken: token)).Instructions);

        var fromBeta = Assert.Single((await beta.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = theirs },
            cancellationToken: token)).Instructions);

        Assert.Equal("alpha's own instruction", fromAlpha.Text);
        Assert.Equal("beta's own instruction", fromBeta.Text);
    }

    [Fact]
    public async Task A_listing_of_one_broker_never_carries_anothers_rows()
    {
        // The wider version of the same worry: not a carried identifier but a shared file. Two
        // brokers, two stores, and a listing that asked for everything.
        var token = TestContext.Current.CancellationToken;

        await using var alpha = await LoopbackBroker.StartAsync(token);
        await using var beta = await LoopbackBroker.StartAsync(token);

        _ = await ToldAsync(alpha, "only in alpha", token);
        _ = await ToldAsync(beta, "only in beta", token);

        var listed = await alpha.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest { PageSize = 100 },
            cancellationToken: token);

        Assert.Equal("only in alpha", Assert.Single(listed.Instructions).Text);
    }
}
