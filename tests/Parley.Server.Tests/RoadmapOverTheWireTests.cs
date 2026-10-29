using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A roadmap from proposal to started item, end to end over a real socket (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Over a socket because the rule crosses the agent and operator boundary.</b> The agent proposes through
/// <c>Agents</c> and the person decides through <c>Operators</c>, two services reached by two clients, so a test
/// calling the broker in process would show a store accepts a row and say nothing about whether the two halves are
/// separable.
/// </para>
/// <para>
/// <b>Nothing here opens a store</b>, which this project forbids: everything asserted is read back through the wire.
/// That is also the stronger claim, since a row nobody can read through the protocol is not evidence the protocol
/// records anything.
/// </para>
/// </remarks>
public sealed class RoadmapOverTheWireTests
{
    private const string Person = "human-user@test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.RoadmapContent Plan(string title = "Billing export") => new()
    {
        Title = title,
        Items =
        {
            new Wire.RoadmapItemDraft { Text = "Add CSV export", IntendedAgent = "billing@test" },
            new Wire.RoadmapItemDraft { Text = "Call it from the report", IntendedAgent = "orders@test", Needs = { 1 } },
        },
    };

    private static async Task<long> ProposeAsync(AttachedClient agent, string title = "Billing export")
    {
        var reply = await agent.Client.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest { Agent = agent.Name, Content = Plan(title) }, cancellationToken: Ct);

        Assert.Empty(reply.Problem);
        Assert.True(reply.ProposalId > 0);

        return reply.ProposalId;
    }

    private static async Task<long> ConfirmedAsync(LoopbackBroker broker, long proposal)
    {
        var operators = broker.ConnectAsOperator();
        var waiting = (await operators.ProposalsAsync(
            new Wire.ProposalsRequest { State = Wire.ProposalState.Waiting }, cancellationToken: Ct))
            .Proposals.Single(p => p.Id == proposal);

        var change = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = proposal, Content = waiting.Content }, cancellationToken: Ct);

        Assert.True(change.Done, change.Problem);

        return change.RoadmapId;
    }

    [Fact]
    public async Task A_revision_names_the_revision_it_was_chosen_against_and_is_refused_once_that_has_moved()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var roadmap = await ConfirmedAsync(broker, await ProposeAsync(orders));
        var revision = await ProposeAsync(orders);

        // No revision named: refused, whatever the roadmap is at.
        var unnamed = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = revision, Content = Plan(), TargetRoadmap = roadmap }, cancellationToken: Ct);
        Assert.False(unnamed.Done);
        Assert.Contains("names the revision", unnamed.Problem, StringComparison.Ordinal);

        // Edited on another surface since revision 1 was shown: refused, and the edit stays.
        var edited = await operators.EditRoadmapAsync(
            new Wire.EditRoadmapRequest { Id = roadmap, Revision = 1, Content = Plan("Billing export, edited") }, cancellationToken: Ct);
        Assert.True(edited.Done, edited.Problem);

        var stale = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = revision, Content = Plan(), TargetRoadmap = roadmap, TargetRevision = 1 },
            cancellationToken: Ct);
        Assert.False(stale.Done);
        Assert.Equal(
            "Billing export, edited",
            (await operators.RoadmapAsync(new Wire.RoadmapRequest { Id = roadmap }, cancellationToken: Ct)).Roadmap!.Title);

        // At the revision it now is: done.
        var current = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = revision, Content = Plan(), TargetRoadmap = roadmap, TargetRevision = edited.Revision },
            cancellationToken: Ct);
        Assert.True(current.Done, current.Problem);
    }

    [Fact]
    public async Task A_proposal_is_waiting_until_a_person_confirms_it_and_then_the_proposer_is_told()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var proposal = await ProposeAsync(orders);

        // Nothing is a roadmap yet, and nothing was written as a message.
        Assert.Empty((await operators.RoadmapsAsync(new Wire.Empty(), cancellationToken: Ct)).Roadmaps);

        var waiting = Assert.Single((await operators.ProposalsAsync(
            new Wire.ProposalsRequest { State = Wire.ProposalState.Waiting }, cancellationToken: Ct)).Proposals);
        Assert.Equal("orders@test", waiting.Proposer);
        Assert.Equal(2, waiting.Content.Items.Count);

        var change = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = proposal, Content = waiting.Content }, cancellationToken: Ct);

        Assert.True(change.Done, change.Problem);
        Assert.Equal(Wire.RoadmapOrigin.Proposed, change.Origin);

        var summary = Assert.Single((await operators.RoadmapsAsync(new Wire.Empty(), cancellationToken: Ct)).Roadmaps);
        Assert.Equal(2, summary.LiveItems);
        Assert.Equal(0, summary.StartedItems);

        var roadmap = (await operators.RoadmapAsync(
            new Wire.RoadmapRequest { Id = summary.Id }, cancellationToken: Ct)).Roadmap!;
        Assert.Equal(["rm1-1", "rm1-2"], roadmap.Items.Select(i => i.ConversationName));
        Assert.False(roadmap.Items[0].Facts.ConversationExists);
        Assert.Equal([1], roadmap.Items[1].Needs);

        // Counted without being taken, and never waking anything (it is not a message).
        Assert.Equal(1, (await orders.Client.WaitingAsync(
            new Wire.WaitingRequest { Agent = orders.Name }, cancellationToken: Ct)).RoadmapNoticesWaiting);

        var inbox = await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

        Assert.Empty(inbox.Messages);
        var notice = Assert.Single(inbox.RoadmapNotices);
        Assert.Equal(Wire.ProposalState.Confirmed, notice.Outcome);
        Assert.Equal("Billing export", notice.Title);

        Assert.Empty((await billing.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: Ct)).RoadmapNotices);
    }

    [Fact]
    public async Task An_agent_cannot_open_an_items_conversation_by_placing_a_file_in_it()
    {
        // A file opens a conversation and writes no message, so the reserved-conversation trigger never sees it. Must fail
        // against admitting the placement, which left the item reading as begun with only the agent in its conversation.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);

        var roadmap = await ConfirmedAsync(broker, await ProposeAsync(orders));

        var placed = await FileWire.PlaceAsync(orders.Client, orders.Name, "notes.md", "# notes"u8.ToArray(), "rm1-1", Ct);

        Assert.NotEqual(Wire.PlaceOutcome.Placed, placed.Outcome);
        Assert.Equal(Wire.FileRejection.ConversationNotAssignable, placed.Rejection);

        // What the agent reads must be true: it holds no requests, so it is not told it does.
        Assert.DoesNotContain("hold requests", placed.Detail, StringComparison.Ordinal);
        Assert.Contains("Leave conversation_id out", placed.Detail, StringComparison.Ordinal);

        var detail = await broker.ConnectAsOperator().RoadmapAsync(new Wire.RoadmapRequest { Id = roadmap }, cancellationToken: Ct);
        Assert.False(detail.Roadmap.Items.Single(i => i.Number == 1).Facts.ConversationExists);
    }

    [Fact]
    public async Task Declining_persists_nothing_and_tells_the_proposer()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var proposal = await ProposeAsync(orders);

        var declined = await operators.DeclineProposalAsync(new Wire.ProposalRequest { Id = proposal }, cancellationToken: Ct);

        Assert.True(declined.Done, declined.Problem);
        Assert.Empty((await operators.RoadmapsAsync(new Wire.Empty(), cancellationToken: Ct)).Roadmaps);

        var inbox = await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);

        Assert.Equal(Wire.ProposalState.Declined, Assert.Single(inbox.RoadmapNotices).Outcome);
    }

    [Fact]
    public async Task The_person_seat_proposes_nothing_and_says_why()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(
            broker, Person, cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);

        var reply = await seat.Client.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest { Agent = Person, Content = Plan() }, cancellationToken: Ct);

        Assert.Equal(0, reply.ProposalId);
        Assert.Contains("Only an agent", reply.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposal_that_breaks_a_line_is_refused_with_a_sentence()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);

        var reply = await orders.Client.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest
            {
                Agent = orders.Name,
                Content = new Wire.RoadmapContent
                {
                    Title = "Plan",
                    Items = { new Wire.RoadmapItemDraft { Text = "one\ntwo" } },
                },
            },
            cancellationToken: Ct);

        Assert.Equal(0, reply.ProposalId);
        Assert.NotEmpty(reply.Problem);
    }

    [Fact]
    public async Task An_intended_agent_who_is_not_in_the_project_is_refused_with_a_sentence_when_proposing_and_when_confirming()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        // billing@test never attached, so item 1 names somebody the project does not have. Told so as a sentence
        // the proposer can send again without, never as a constraint's text.
        var proposed = await orders.Client.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest { Agent = orders.Name, Content = Plan() }, cancellationToken: Ct);

        Assert.Equal(0, proposed.ProposalId);
        Assert.Contains("billing@test", proposed.Problem, StringComparison.Ordinal);

        // The person's own edit can name somebody the project does not have as well.
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var proposal = await ProposeAsync(orders);

        var stranger = Plan();
        stranger.Items[0].IntendedAgent = "nobody@test";

        var change = await operators.ConfirmProposalAsync(
            new Wire.ConfirmProposalRequest { Id = proposal, Content = stranger }, cancellationToken: Ct);

        Assert.False(change.Done);
        Assert.Contains("nobody@test", change.Problem, StringComparison.Ordinal);
        Assert.Empty((await operators.RoadmapsAsync(new Wire.Empty(), cancellationToken: Ct)).Roadmaps);
    }

    [Fact]
    public async Task Starting_an_item_is_the_persons_request_into_the_reserved_conversation()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        await using var seat = await AttachedClient.AttachAsync(
            broker, Person, cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);
        var operators = broker.ConnectAsOperator();

        var roadmapId = await ConfirmedAsync(broker, await ProposeAsync(orders));

        var started = Assert.Single((await seat.Client.RequestAsync(
            new Wire.OpenRequest
            {
                From = Person,
                To = billing.Name,
                ResponderModel = "claude-opus-5",
                PayloadJson = """{"ask":"Add CSV export"}""",
                ConversationId = "rm1-1",
            },
            cancellationToken: Ct)).Outcomes);

        Assert.True(started.Ok, started.Rejection.ToString());

        var roadmap = (await operators.RoadmapAsync(new Wire.RoadmapRequest { Id = roadmapId }, cancellationToken: Ct)).Roadmap!;
        Assert.True(roadmap.Items[0].Facts.ConversationExists);
        Assert.Equal(1, roadmap.Items[0].Facts.OpenExchanges);
        Assert.False(roadmap.Items[0].Facts.Collected);
        Assert.False(roadmap.Items[1].Facts.ConversationExists);

        // Both reads a surface opens a conversation with name the item, by the reservation.
        var export = await operators.ExportAsync(new Wire.ExportRequest { ConversationId = "rm1-1" }, cancellationToken: Ct);
        Assert.Equal(1, export.Item.ItemNumber);
        Assert.Equal("Billing export", export.Item.Title);

        var listed = (await operators.ListConversationsAsync(
            new Wire.ListConversationsRequest(), cancellationToken: Ct)).Conversations.Single(c => c.ConversationId == "rm1-1");
        Assert.Equal(1, listed.Item.ItemNumber);

        // The agent collects it as an ordinary request, from the person.
        var inbox = await billing.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: Ct);

        Assert.Equal(Person, Assert.Single(inbox.Messages).Message.From);

        var after = (await operators.RoadmapAsync(new Wire.RoadmapRequest { Id = roadmapId }, cancellationToken: Ct)).Roadmap!;
        Assert.True(after.Items[0].Facts.Collected);
    }

    [Fact]
    public async Task An_agent_cannot_open_an_items_conversation_before_the_person_does()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await ConfirmedAsync(broker, await ProposeAsync(orders));

        // Refused either as a status or as an unsuccessful outcome; what matters is that nothing was admitted and
        // the person's conversation was not captured.
        try
        {
            var reply = await billing.Client.RequestAsync(
                new Wire.OpenRequest
                {
                    From = billing.Name,
                    To = orders.Name,
                    ResponderModel = "claude-opus-5",
                    PayloadJson = """{"ask":"take this item"}""",
                    ConversationId = "rm1-1",
                },
                cancellationToken: Ct);

            Assert.All(reply.Outcomes, o => Assert.False(o.Ok));
        }
        catch (RpcException refused)
        {
            Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        }

        var roadmap = (await operators.RoadmapsAsync(new Wire.Empty(), cancellationToken: Ct)).Roadmaps.Single();
        Assert.Equal(0, roadmap.StartedItems);
    }

    [Fact]
    public async Task Two_surfaces_editing_at_once_cannot_overwrite_each_other()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var id = await ConfirmedAsync(broker, await ProposeAsync(orders));

        var edit = new Wire.RoadmapContent
        {
            Title = "Billing export, reworded",
            Items =
            {
                new Wire.RoadmapItemDraft { Number = 1, Text = "Add CSV export" },
                new Wire.RoadmapItemDraft { Number = 2, Text = "Call it from the report", Needs = { 1 } },
            },
        };

        var first = await operators.EditRoadmapAsync(
            new Wire.EditRoadmapRequest { Id = id, Revision = 1, Content = edit }, cancellationToken: Ct);
        var second = await operators.EditRoadmapAsync(
            new Wire.EditRoadmapRequest { Id = id, Revision = 1, Content = edit }, cancellationToken: Ct);

        Assert.True(first.Done, first.Problem);
        Assert.Equal(2, first.Revision);
        Assert.False(second.Done);
        Assert.Contains("revision 2", second.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_saying_it_is_finished_with_an_items_conversation_is_told_only_the_person_closes_it()
    {
        // SPEC.md section 5.7: a closed conversation never reopens and an item has only the one.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@test", cancellationToken: Ct);
        await using var seat = await AttachedClient.AttachAsync(
            broker, Person, cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);

        _ = await ConfirmedAsync(broker, await ProposeAsync(orders));

        var started = Assert.Single((await seat.Client.RequestAsync(
            new Wire.OpenRequest
            {
                From = Person,
                To = billing.Name,
                ResponderModel = "claude-opus-5",
                PayloadJson = """{"ask":"Add CSV export"}""",
                ConversationId = "rm1-1",
            },
            cancellationToken: Ct)).Outcomes);
        Assert.True(started.Ok, started.Rejection.ToString());

        var said = await billing.Client.ConcludeAsync(
            new Wire.ConcludeRequest { From = billing.Name, ConversationId = "rm1-1", Vote = Wire.Vote.Done },
            cancellationToken: Ct);

        Assert.False(said.Ok);
        Assert.Equal(Wire.ClosureError.PersonCloses, said.Refusal);
        Assert.False(said.Concluded);
    }
}
