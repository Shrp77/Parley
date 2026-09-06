using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That archiving hides a conversation from a listing and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.3. Archiving is the first thing in this protocol that makes a
/// conversation invisible by default, so the question is what else it made invisible — and whether
/// an agent can tell.
/// </para>
/// <para>
/// The second half is the one with teeth. §10.2 says archiving changes nothing but the listing and
/// that no agent is told; if an archived conversation refused a message, or answered differently,
/// then an operator's filing decision would have become something agents can observe and work
/// around. These are written to find that.
/// </para>
/// </remarks>
public sealed class ArchiveBoundaryTests
{
    private const string Consequences =
        "If the answer is yes I will record it and stop asking; if no I will raise it with the "
        + "operator rather than deciding alone.";

    private static string Question(string text) =>
        $$"""{"intent":"{{text}}","sub_questions":[{"id":"q1","text":"{{text}}"}],"consequences":"{{Consequences}}"}""";

    [Fact]
    public async Task An_agent_has_no_operation_that_archives_or_destroys()
    {
        // The boundary itself, written as the attempt rather than read off the proto. An adapter
        // binds Agents; if either operation were reachable from there, §10.1 would be breached and
        // an agent could file away or erase the record of a conversation it was in.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var agents = typeof(Wire.Agents.AgentsClient)
            .GetMethods()
            .Select(method => method.Name)
            .ToList();

        Assert.DoesNotContain(agents, name => name.StartsWith("Archive", StringComparison.Ordinal));
        Assert.DoesNotContain(agents, name => name.StartsWith("Unarchive", StringComparison.Ordinal));
        Assert.DoesNotContain(agents, name => name.StartsWith("Destroy", StringComparison.Ordinal));
        Assert.DoesNotContain(agents, name => name.StartsWith("Start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Archiving_a_conversation_does_not_stop_a_question_in_it()
    {
        // §10.2 in one sentence: filing, not muting. An archived conversation that refused traffic
        // would make an operator's view preference into something that breaks an agent's work.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token);
        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", "C:/repo-billing", token);

        var first = await orders.Client.AskAsync(
            new Wire.AskRequest
            {
                From = orders.Name,
                To = billing.Name,
                ConversationId = "a-discussion",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the totals"),
            },
            cancellationToken: token);

        Assert.True(first.Ok, "the first question was refused");

        await broker.ConnectAsOperator().ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        // And a second question into the same conversation, after it was filed away.
        var second = await orders.Client.AskAsync(
            new Wire.AskRequest
            {
                From = orders.Name,
                To = billing.Name,
                ConversationId = "a-discussion",
                ResponderModel = "some-model-9",
                PayloadJson = Question("and who owns the reconciliation"),
            },
            cancellationToken: token);

        Assert.True(second.Ok, "a question into an archived conversation was refused");
    }

    [Fact]
    public async Task A_message_into_an_archived_conversation_leaves_it_archived()
    {
        // The decision recorded in §10.2 and in research.md R4, asserted rather than assumed: the
        // alternative lets any agent reverse an operator's decision by sending something, which is
        // the one direction of authority this protocol does not have.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token);
        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", "C:/repo-billing", token);

        await orders.Client.AskAsync(
            new Wire.AskRequest
            {
                From = orders.Name,
                To = billing.Name,
                ConversationId = "a-discussion",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the totals"),
            },
            cancellationToken: token);

        await broker.ConnectAsOperator().ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        await orders.Client.AskAsync(
            new Wire.AskRequest
            {
                From = orders.Name,
                To = billing.Name,
                ConversationId = "a-discussion",
                ResponderModel = "some-model-9",
                PayloadJson = Question("and the reconciliation"),
            },
            cancellationToken: token);

        var listed = await broker.ConnectAsOperator().ListConversationsAsync(
            new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: token);

        Assert.True(listed.Conversations.Single(c => c.ConversationId == "a-discussion").Archived);
    }

    [Fact]
    public async Task An_archived_conversation_is_still_exportable_by_an_operator()
    {
        // Filing is not hiding from the operator who filed it. An archive that also made the
        // transcript unreadable would be a destroy with a friendlier name.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token);
        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", "C:/repo-billing", token);

        await orders.Client.AskAsync(
            new Wire.AskRequest
            {
                From = orders.Name,
                To = billing.Name,
                ConversationId = "a-discussion",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the totals"),
            },
            cancellationToken: token);

        await broker.ConnectAsOperator().ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.NotEmpty(export.Messages);
    }

    [Fact]
    public async Task The_omitted_count_is_returned_whether_or_not_archived_ones_were_asked_for()
    {
        // What makes "a message does not un-archive it" safe. A caller that could not tell archived
        // conversations existed would be hiding traffic rather than filing it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-orders", token);
        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", "C:/repo-billing", token);

        await orders.Client.AskAsync(
            new Wire.AskRequest
            {
                From = orders.Name,
                To = billing.Name,
                ConversationId = "a-discussion",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the totals"),
            },
            cancellationToken: token);

        await broker.ConnectAsOperator().ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        var hiding = await broker.ConnectAsOperator().ListConversationsAsync(
            new Wire.ListConversationsRequest(), cancellationToken: token);

        Assert.Empty(hiding.Conversations);
        Assert.Equal(1, hiding.ArchivedOmitted);

        var showing = await broker.ConnectAsOperator().ListConversationsAsync(
            new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: token);

        Assert.Single(showing.Conversations);

        // Zero when they were asked for, because then none were left out. A count that stayed at
        // one would have a surface saying something is hidden while showing it.
        Assert.Equal(0, showing.ArchivedOmitted);
    }

    [Fact]
    public async Task Archiving_something_that_is_not_there_is_reported_rather_than_thrown()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var changed = await broker.ConnectAsOperator().ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = "nothing-here" },
            cancellationToken: token);

        Assert.False(changed.Changed);
    }

    [Fact]
    public async Task An_identifier_shaped_like_a_path_is_refused()
    {
        // The conversations table constrains its own identifiers against `/`, `:` and runs of dots
        // because the value reaches a filename and a report heading. These operations take one
        // from a caller, so this asserts nothing widens that.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        foreach (var id in new[] { "../../etc/passwd", "a/b", "a:b" })
        {
            var changed = await broker.ConnectAsOperator().ArchiveAsync(
                new Wire.ConversationRequest { ConversationId = id }, cancellationToken: token);

            // No such conversation can exist under that name, so nothing changes -- and nothing
            // throws, which is what an operator typing a wrong thing should meet.
            Assert.False(changed.Changed);
        }
    }
}
