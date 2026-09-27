using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// An agent proposing what it owns, and a person accepting it, end to end over a socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Over a real socket because the rule being checked crosses the agent/operator boundary.</b> The
/// agent proposes through <c>Agents</c> and the person records through <c>Operators</c>, and the whole
/// point of §10.2's split is that those are two services reached by two clients — so a test calling
/// the broker in process would prove the store accepts a row and say nothing about whether the two
/// halves are separable, which is the property that matters.
/// </para>
/// <para>
/// <b>Nothing here opens a store</b>, which this project forbids. Everything asserted is read back
/// through the wire: what the registry says <c>owns</c> is, and what the reply says is live and
/// pending. That is also the stronger assertion — a row nobody can read through the protocol is not
/// evidence that the protocol records anything.
/// </para>
/// </remarks>
public sealed class OwnershipOverTheWireTests
{
    private static Wire.OpenRequest Raw(string from, string to, string payload, string conversation) =>
        new()
        {
            From = from,
            To = to,
            ResponderModel = "claude-opus-5",
            PayloadJson = payload,
            ConversationId = conversation,
        };

    [Fact]
    public async Task A_line_an_agent_proposed_is_recorded_as_the_agents_and_leaves_the_registry_alone()
    {
        // **The first assertion is that `owns` is untouched, which is the obvious implementation and
        // is the route around Principle III that §17 question 10 exists to keep shut.** Recording an
        // acceptance must not make the line live: the registry is filled from the launch environment
        // when the agent attaches (§12.3), so a broker that wrote it here would be inventing a fact
        // about a repository it cannot see.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var person = await AttachedClient.AttachAsync(
            broker, "arvind@alpha", "C:/repo-person", token);
        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token);

        var discussion = "owns-orders-20260927-090000";

        // The person asks, as one ordinary Request in a conversation whose identifier is how a surface
        // finds the discussion again -- never a marker in the payload (§13).
        var asked = Assert.Single((await person.Client.RequestAsync(
            Raw(person.Name, orders.Name, """{"ask":"What do you own, and what do you not?"}""", discussion),
            cancellationToken: token)).Outcomes);

        Assert.True(asked.Ok, asked.Rejection.ToString());

        // The agent answers. This is the proposal, and it is the agent's own words.
        await orders.Client.ResultAsync(
            new Wire.ResultRequest
            {
                From = orders.Name,
                ExchangeId = asked.ExchangeId,
                PayloadJson =
                    """{"outcomes":[{"id":"request","outcome":"answered","text":"Orders and invoicing. Not stock."}]}""",
                ResponderModel = "claude-opus-5",
            },
            cancellationToken: token);

        var operators = broker.ConnectAsOperator();

        var recorded = await operators.RecordOwnershipAsync(
            new Wire.RecordOwnershipRequest
            {
                Agent = orders.Name,
                Statement = "Orders and invoicing. Not stock.",
                Source = Wire.OwnershipSource.Proposed,
                ProposedLength = "Orders and invoicing. Not stock.".Length,
                ConversationId = discussion,
            },
            cancellationToken: token);

        Assert.True(recorded.Recorded, recorded.Problem);

        // **Pending is a comparison of two observations, not a third flag.** The agent attached with no
        // ownership line, so what peers read is still nothing and the reply must say so.
        Assert.True(recorded.Pending);
        Assert.Empty(recorded.LiveStatement);

        var registry = await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token);
        var entry = Assert.Single(registry.Rows, r => r.Name == orders.Name);

        Assert.Empty(entry.Owns);
    }

    [Fact]
    public async Task An_accepted_line_that_peers_already_read_is_not_reported_as_pending()
    {
        // The other side of the comparison. An agent whose launch environment already carries the line
        // is not pending, and a surface that kept a flag instead would have to be told to clear it --
        // which is the third state nobody remembers to maintain.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token, "Orders and invoicing.");

        var recorded = await broker.ConnectAsOperator().RecordOwnershipAsync(
            new Wire.RecordOwnershipRequest
            {
                Agent = orders.Name,
                Statement = "Orders and invoicing.",
                Source = Wire.OwnershipSource.Composed,
                ConversationId = string.Empty,
            },
            cancellationToken: token);

        Assert.True(recorded.Recorded, recorded.Problem);
        Assert.False(recorded.Pending);
        Assert.Equal("Orders and invoicing.", recorded.LiveStatement);
    }

    [Fact]
    public async Task An_agent_nothing_has_registered_is_refused_rather_than_recorded_against()
    {
        // §14 has the broker refuse an unregistered agent every operation, and this is not an
        // exception: a record against a name nothing has ever registered is a decision about an agent
        // that does not exist, and the line would be written where nobody could ever read it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var recorded = await broker.ConnectAsOperator().RecordOwnershipAsync(
            new Wire.RecordOwnershipRequest
            {
                Agent = "nobody@alpha",
                Statement = "Everything.",
                Source = Wire.OwnershipSource.Composed,
            },
            cancellationToken: token);

        Assert.False(recorded.Recorded);
        Assert.NotEmpty(recorded.Problem);
    }

    [Fact]
    public async Task A_refusal_comes_back_as_a_reply_rather_than_as_a_transport_fault()
    {
        // **The shape, not the sentence.** A refusal arriving as an RpcException is indistinguishable
        // from a broken deployment on the operator's side, which is the distinction every refusal in
        // this protocol is written to preserve. A blank statement is refused by the rule in storage.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token);

        var recorded = await broker.ConnectAsOperator().RecordOwnershipAsync(
            new Wire.RecordOwnershipRequest
            {
                Agent = orders.Name,
                Statement = "   ",
                Source = Wire.OwnershipSource.Composed,
            },
            cancellationToken: token);

        Assert.False(recorded.Recorded);
        Assert.NotEmpty(recorded.Problem);
    }
}
