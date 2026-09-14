using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What the wake outcome tells a sender about somebody it is not talking to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new field on a participant-facing reply is a disclosure until argued otherwise.</b> §10.1 withholds from a
/// participant everything about pairs it is not part of.
/// </para>
/// <para>
/// <b>The argument, made rather than assumed.</b> Every outcome describes the receiver's own state: whether it is
/// running, and whether a turn is already on its way. Neither names another participant, carries a message, or
/// says who caused it. Being running is already public through the roster, and "a turn is coming" is the same class
/// of fact about the same agent.
/// </para>
/// <para>
/// <b>What is conceded.</b> A participant polling a receiver with Requests could infer coarse activity from the
/// outcome changing, which it could already infer from the roster's presence column. Recorded as low.
/// </para>
/// </remarks>
public sealed class WakeDisclosureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static async Task RegisterAndCloseAsync(LoopbackBroker broker, string name, CancellationToken token)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: token))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    [Fact]
    public void The_outcome_can_carry_no_identity_and_no_text()
    {
        // Structural rather than behavioural: an enum cannot grow a name or a payload without somebody changing its
        // type, which is a decision rather than an oversight.
        Assert.True(typeof(Wire.WakeOutcome).IsEnum);

        foreach (var member in Enum.GetNames<Wire.WakeOutcome>())
        {
            Assert.DoesNotContain("@", member, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_third_partys_wake_is_not_attributed_to_anybody()
    {
        // billing learns that orders has a turn coming. It must not learn that docs caused it: that is a fact about
        // a pair billing is not in.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", cancellationToken: token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", cancellationToken: token);

        var first = Assert.Single((await docs.Client.RequestAsync(Requests.Open("docs@alpha", "orders@alpha", "from the documentation team", "c1"), cancellationToken: token)).Outcomes);
        Assert.Equal(Wire.WakeOutcome.Started, first.Wake);

        var second = await billing.Client.RequestAsync(Requests.Open("billing@alpha", "orders@alpha", "from billing", "c2"), cancellationToken: token);
        Assert.Equal(Wire.WakeOutcome.AlreadyComing, Assert.Single(second.Outcomes).Wake);

        // The whole reply, not just the field: nothing in it names the other sender or repeats a word they wrote.
        var whole = second.ToString();

        Assert.DoesNotContain("docs", whole, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("documentation", whole, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_wake_cannot_be_used_to_start_a_role_the_machine_did_not_offer()
    {
        // The guarantee that keeps this from being remote execution. A node starts what its own catalogue names and
        // nothing else, and a message is not a way round that.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);

        await RegisterAndCloseAsync(broker, "orders@alpha", token);

        Assert.Equal(WakeOutcome.Unreachable, broker.Runtime.RequestAttentionForMail(AgentName.Create("orders@alpha"), "told"));
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(300)));
    }
}
