using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a roadmap proposal may name as its source only a conversation its proposer took part in (SPEC.md §10.1).
/// </summary>
/// <remarks>
/// <b>Found by review, round 32.</b> The source was stored as given: an agent outside a conversation put its plan in the
/// person's view of that conversation, shown as where it came from, and a conversation that did not exist came back as a
/// foreign key's text, which said which conversations exist.
/// </remarks>
public sealed class AProposalComesFromItsProposersConversationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.ProposeRoadmapRequest Plan(string agent, string conversation) => new()
    {
        Agent = agent,
        ConversationId = conversation,
        Content = new Wire.RoadmapContent { Title = "Plan", Items = { new Wire.RoadmapItemDraft { Text = "Do it" } } },
    };

    [Fact]
    public async Task A_conversation_the_proposer_was_not_in_is_refused_as_one_that_does_not_exist()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, "inventory@test", cancellationToken: Ct);

        var opened = Assert.Single((await billing.Client.RequestAsync(new Wire.OpenRequest
        {
            From = billing.Name, To = inventory.Name, ResponderModel = "m",
            PayloadJson = """{"ask":"between billing and inventory"}""", ConversationId = "private-talk",
        }, cancellationToken: Ct)).Outcomes);
        Assert.True(opened.Ok);

        var outside = await orders.Client.ProposeRoadmapAsync(Plan(orders.Name, "private-talk"), cancellationToken: Ct);
        var nowhere = await orders.Client.ProposeRoadmapAsync(Plan(orders.Name, "no-such-conversation"), cancellationToken: Ct);

        Assert.Equal(0, outside.ProposalId);
        Assert.Equal(0, nowhere.ProposalId);
        Assert.NotEmpty(outside.Problem);
        Assert.Equal(outside.Problem, nowhere.Problem);

        // Each side of the conversation may name it, and a proposal naming none is stored as before.
        var inside = await inventory.Client.ProposeRoadmapAsync(Plan(inventory.Name, "private-talk"), cancellationToken: Ct);
        var opener = await billing.Client.ProposeRoadmapAsync(Plan(billing.Name, "private-talk"), cancellationToken: Ct);
        var unnamed = await orders.Client.ProposeRoadmapAsync(Plan(orders.Name, string.Empty), cancellationToken: Ct);

        Assert.Equal(string.Empty, inside.Problem);
        Assert.Equal(string.Empty, opener.Problem);
        Assert.Equal(string.Empty, unnamed.Problem);

        var listed = (await broker.ConnectAsOperator().ProposalsAsync(
            new Wire.ProposalsRequest { State = Wire.ProposalState.Waiting }, cancellationToken: Ct)).Proposals;

        Assert.Equal(3, listed.Count);
        Assert.DoesNotContain(listed, p => p.Proposer == orders.Name && p.SourceConversation == "private-talk");
    }
}
