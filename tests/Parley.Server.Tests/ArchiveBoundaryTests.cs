using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That archiving hides a conversation from a listing and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Archiving is the first thing in this protocol that makes a conversation invisible by default, so the question
/// is what else it made invisible — and whether an agent can tell.
/// </para>
/// <para>
/// The second half is the one with teeth. §10.2 says archiving changes nothing but the listing and that no agent is
/// told; if an archived conversation refused a message, then a person's filing decision would have become
/// something agents can observe and work around.
/// </para>
/// </remarks>
[Integration]
public sealed class ArchiveBoundaryTests
{
    private static async Task<(AttachedClient Orders, AttachedClient Billing)> TwoAsync(LoopbackBroker broker, CancellationToken token) =>
        (await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo-orders", token),
         await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/repo-billing", token));

    private static async Task<Wire.RequestOutcome> SendAsync(AttachedClient from, AttachedClient to, string text, CancellationToken token) =>
        Assert.Single((await from.Client.RequestAsync(Requests.Open(from.Name, to.Name, text, "a-discussion"), cancellationToken: token)).Outcomes);

    [Fact]
    public void An_agent_has_no_operation_that_archives_or_destroys()
    {
        // The boundary itself. An adapter binds Agents; if either operation were reachable from there, §10.1 would be
        // breached and an agent could file away or erase the record of a conversation it was in.
        var agents = typeof(Wire.Agents.AgentsClient).GetMethods().Select(method => method.Name).ToList();

        Assert.DoesNotContain(agents, name => name.StartsWith("Archive", StringComparison.Ordinal));
        Assert.DoesNotContain(agents, name => name.StartsWith("Unarchive", StringComparison.Ordinal));
        Assert.DoesNotContain(agents, name => name.StartsWith("Destroy", StringComparison.Ordinal));
        Assert.DoesNotContain(agents, name => name.StartsWith("Start", StringComparison.Ordinal));
        Assert.DoesNotContain(agents, name => name.StartsWith("ForceConclude", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Archiving_a_conversation_does_not_stop_a_request_in_it()
    {
        // Filing, not muting. An archived conversation that refused traffic would make a view preference into
        // something that breaks an agent's work.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        var (orders, billing) = await TwoAsync(broker, token);
        await using var _ = orders;
        await using var __ = billing;

        Assert.True((await SendAsync(orders, billing, "who owns the totals", token)).Ok, "the first request was refused");

        await broker.ConnectAsOperator().ArchiveAsync(new Wire.ConversationRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.True((await SendAsync(orders, billing, "and who owns the reconciliation", token)).Ok, "a request into an archived conversation was refused");
    }

    [Fact]
    public async Task A_message_into_an_archived_conversation_leaves_it_archived()
    {
        // The alternative lets any agent reverse a person's decision by sending something, which is the one
        // direction of authority this protocol does not have.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        var (orders, billing) = await TwoAsync(broker, token);
        await using var _ = orders;
        await using var __ = billing;

        await SendAsync(orders, billing, "who owns the totals", token);
        await broker.ConnectAsOperator().ArchiveAsync(new Wire.ConversationRequest { ConversationId = "a-discussion" }, cancellationToken: token);
        await SendAsync(orders, billing, "and the reconciliation", token);

        var listed = await broker.ConnectAsOperator().ListConversationsAsync(new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: token);

        Assert.True(listed.Conversations.Single(c => c.ConversationId == "a-discussion").Archived);
    }

    [Fact]
    public async Task An_archived_conversation_is_still_exportable_by_an_operator()
    {
        // Filing is not hiding from the person who filed it. An archive that also made the transcript unreadable
        // would be a destroy with a friendlier name.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        var (orders, billing) = await TwoAsync(broker, token);
        await using var _ = orders;
        await using var __ = billing;

        await SendAsync(orders, billing, "who owns the totals", token);
        await broker.ConnectAsOperator().ArchiveAsync(new Wire.ConversationRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        var export = await broker.ConnectAsOperator().ExportAsync(new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.NotEmpty(export.Messages);
    }

    [Fact]
    public async Task The_omitted_count_is_returned_whether_or_not_archived_ones_were_asked_for()
    {
        // A caller that could not tell archived conversations existed would be hiding traffic rather than filing it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        var (orders, billing) = await TwoAsync(broker, token);
        await using var _ = orders;
        await using var __ = billing;

        await SendAsync(orders, billing, "who owns the totals", token);
        await broker.ConnectAsOperator().ArchiveAsync(new Wire.ConversationRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        var hiding = await broker.ConnectAsOperator().ListConversationsAsync(new Wire.ListConversationsRequest(), cancellationToken: token);

        Assert.Empty(hiding.Conversations);
        Assert.Equal(1, hiding.ArchivedOmitted);

        var showing = await broker.ConnectAsOperator().ListConversationsAsync(new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: token);

        Assert.Single(showing.Conversations);
        Assert.Equal(0, showing.ArchivedOmitted);
    }

    [Fact]
    public async Task Archiving_something_that_is_not_there_is_reported_rather_than_thrown()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        Assert.False((await broker.ConnectAsOperator().ArchiveAsync(new Wire.ConversationRequest { ConversationId = "nothing-here" }, cancellationToken: token)).Changed);
    }

    [Fact]
    public async Task An_identifier_shaped_like_a_path_is_refused()
    {
        // The conversations table constrains its identifiers against `/`, `:` and runs of dots because the value
        // reaches a filename and a report heading.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        foreach (var id in new[] { "../../etc/passwd", "a/b", "a:b" })
        {
            Assert.False((await broker.ConnectAsOperator().ArchiveAsync(new Wire.ConversationRequest { ConversationId = id }, cancellationToken: token)).Changed);
        }
    }
}
