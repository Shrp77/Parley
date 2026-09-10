using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What the wake outcome tells a sender about somebody it is not talking to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new field on an agent-facing reply is a disclosure until argued otherwise.</b> §10.1
/// withholds from an agent everything about pairs it is not part of, and the agent-facing roster
/// drops workspace paths for the same reason — so a field that says "somebody else is already being
/// seen to" deserves the argument rather than the benefit of the doubt.
/// </para>
/// <para>
/// <b>The argument, made rather than assumed.</b> Every outcome describes the <em>recipient's own
/// state</em>: whether it is running, and whether a turn is already on its way to it. Neither names
/// a peer, carries a message, or says who caused it. Being running is already public — §4.0 makes an
/// open connection the evidence an agent is reachable, and the roster publishes it — and "a turn is
/// coming" is the same class of fact about the same agent. Nothing here crosses a pair boundary.
/// </para>
/// <para>
/// <b>What is conceded.</b> A peer polling a recipient with notifications could infer coarse
/// activity from the outcome changing, which it could already infer from the roster's presence
/// column. Recorded in <c>REVIEW-LEDGER.md</c> as low rather than argued away.
/// </para>
/// </remarks>
public sealed class WakeDisclosureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static async Task RegisterAndCloseAsync(
        LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    [Fact]
    public void The_outcome_can_carry_no_identity_and_no_text()
    {
        // Structural rather than behavioural, and that is the point: an enum cannot grow a name or
        // a payload without somebody changing its type, which is a decision rather than an
        // oversight. Principle IV — a field that can hold a paragraph eventually holds one.
        Assert.True(typeof(Wire.WakeOutcome).IsEnum);

        // And every member is a bare state, not a state about somebody.
        foreach (var member in Enum.GetNames<Wire.WakeOutcome>())
        {
            Assert.DoesNotContain("@", member, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_third_partys_wake_is_not_attributed_to_anybody()
    {
        // `billing` learns that `orders` has a turn coming. It must not learn that `docs` caused
        // it: that is a fact about a pair `billing` is not in, which is exactly what §10.1
        // withholds.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", cancellationToken: token);
        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var first = await docs.Client.NotifyAsync(
            new Wire.NotifyRequest
            {
                From = "docs@alpha",
                To = "orders@alpha",
                ResponderModel = "test",
                PayloadJson = """{"text":"from docs"}""",
                ConversationId = "c1"
            },
            cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.Started, first.Wake);

        var second = await billing.Client.NotifyAsync(
            new Wire.NotifyRequest
            {
                From = "billing@alpha",
                To = "orders@alpha",
                ResponderModel = "test",
                PayloadJson = """{"text":"from billing"}""",
                ConversationId = "c2"
            },
            cancellationToken: token);

        Assert.Equal(Wire.WakeOutcome.AlreadyComing, second.Wake);

        // The whole reply, not just the field: nothing anywhere in it names the other sender or
        // repeats a word of what they said.
        var whole = second.ToString();

        Assert.DoesNotContain("docs", whole, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from docs", whole, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_wake_cannot_be_used_to_start_a_role_the_machine_did_not_offer()
    {
        // The guarantee that keeps this from being remote execution. A node starts what its own
        // catalogue names and nothing else, and a notification is not a way round that — §4 keeps
        // how to start a repository on the machine it lives on, never in the broker.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        Assert.Equal(
            WakeOutcome.Unreachable,
            broker.Runtime.RequestAttentionForMail(AgentName.Create("orders@alpha"), "told"));

        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_sender_cannot_learn_that_an_agent_exists_by_the_wake_alone()
    {
        // A notification to a name nothing has registered is refused as unroutable before any wake
        // is considered, so the outcome cannot be used to probe for which names exist — it is the
        // refusal that answers, and that behaviour predates this feature.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", cancellationToken: token);

        var reply = await billing.Client.NotifyAsync(
            new Wire.NotifyRequest
            {
                From = "billing@alpha",
                To = "orders@alpha",
                ResponderModel = "test",
                PayloadJson = """{"text":"anyone there?"}""",
                ConversationId = "c1"
            },
            cancellationToken: token);

        Assert.False(reply.Ok);
        Assert.Equal(Wire.NotifyRejection.RecipientUnroutable, reply.Refusal);

        // And nothing was started for a name that does not exist.
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }
}
