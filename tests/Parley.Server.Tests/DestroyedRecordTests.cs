using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That asking for a destroyed conversation is answered rather than met with nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is a requirement rather than a nicety: an empty export cannot be told
/// from a store that lost something. That is the same failure `waiting()` exists to prevent
/// elsewhere — an absence that could equally be two very different things — and it is the whole
/// reason §14 keeps a record at all.
/// </para>
/// <para>
/// <b>Answered by <c>export</c> rather than by an operation of its own</b>, because somebody
/// arrives at this question holding an identifier and asking for the conversation. An operator who
/// had to already know it was destroyed in order to ask about its destruction would not have been
/// helped.
/// </para>
/// </remarks>
[Integration]
public sealed class DestroyedRecordTests
{
    private static async Task<LoopbackBroker> WithConversationAsync(CancellationToken token)
    {
        var broker = await LoopbackBroker.StartAsync(token);

        var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo-o", token);
        var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/repo-b", token);

        await orders.Client.RequestAsync(
            Requests.Open(orders.Name, billing.Name, "who owns the totals", "a-discussion"), cancellationToken: token);

        await orders.DisposeAsync();
        await billing.DisposeAsync();

        return broker;
    }

    [Fact]
    public async Task Exporting_a_destroyed_conversation_returns_the_record_and_no_messages()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await WithConversationAsync(token);

        await broker.ConnectAsOperator().DestroyAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.Empty(export.Messages);
        Assert.NotNull(export.Destroyed);
        Assert.Equal("a-discussion", export.Destroyed.ConversationId);
        Assert.Equal(1, export.Destroyed.Messages);
        Assert.Contains("orders@alpha", export.Destroyed.Participants);
        Assert.Contains("billing@alpha", export.Destroyed.Participants);
    }

    [Fact]
    public async Task An_identifier_that_never_named_anything_returns_neither()
    {
        // The distinction that makes the record worth having. Destroyed and never-existed are two
        // answers, and a reply that could not tell them apart would leave an operator exactly where
        // they were before §14 kept anything.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "never-happened" }, cancellationToken: token);

        Assert.Empty(export.Messages);
        Assert.Null(export.Destroyed);
    }

    [Fact]
    public async Task A_conversation_that_still_exists_carries_no_record()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await WithConversationAsync(token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.NotEmpty(export.Messages);
        Assert.Null(export.Destroyed);
    }

    [Fact]
    public async Task The_record_carries_nothing_of_what_was_said()
    {
        // §9.4 argues that a log kept past its usefulness grows the blast radius of a leaked
        // database. Keeping content past a destroy is that argument inverted, so the whole reply is
        // read as text and the question's own words must not be in it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await WithConversationAsync(token);

        await broker.ConnectAsOperator().DestroyAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        var whole = export.ToString();

        Assert.DoesNotContain("who owns the totals", whole, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_cannot_read_the_record()
    {
        // It names who was party to a conversation across the deployment, which is the disclosure
        // §10.1 withholds. It is on the operator's export and export is not on the agent surface.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var agents = typeof(Wire.Agents.AgentsClient).GetMethods().Select(m => m.Name).ToList();

        Assert.DoesNotContain(agents, name => name.StartsWith("Export", StringComparison.Ordinal));
    }
}
