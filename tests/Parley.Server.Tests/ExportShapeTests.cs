using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That export still answers everything it answered before, now that it answers one thing more.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.9 — the last for this lens. Runs 1.7 and 1.8 asked about the
/// new record; this asks the question a new field always raises and nobody thinks to: whether the
/// old answers still come back unchanged.
/// </para>
/// <para>
/// <b>The field is on the reply, not on a new operation, which is exactly why this matters.</b>
/// `export` is the one query that reads across agents and it is what `parley export` runs. A change
/// to its shape reaches every existing caller, including ones that predate the field entirely.
/// </para>
/// </remarks>
public sealed class ExportShapeTests
{
    private const string Consequences =
        "If the answer is yes I will record it and stop asking; if no I will raise it with the "
        + "operator rather than deciding alone.";

    private static string Question(string text) =>
        $$"""{"intent":"{{text}}","sub_questions":[{"id":"q1","text":"{{text}}"}],"consequences":"{{Consequences}}"}""";

    private static async Task TalkAsync(LoopbackBroker broker, CancellationToken token)
    {
        await using var orders = await AttachedClient.AttachAsync(
            broker, "orders@alpha", "C:/repo-o", token);
        await using var billing = await AttachedClient.AttachAsync(
            broker, "billing@alpha", "C:/repo-b", token);

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
    }

    [Fact]
    public async Task An_ordinary_export_still_carries_its_messages_and_its_exchange_states()
    {
        // The regression a new field invites: the two halves that travel together, still doing so.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await TalkAsync(broker, token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.NotEmpty(export.Messages);
        Assert.NotEmpty(export.Exchanges);
        Assert.Contains("who owns the totals", export.Messages[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_extra_read_does_not_happen_when_there_are_messages()
    {
        // Asserted through the answer rather than by counting queries: a conversation with messages
        // comes back with no record, which is what says the second read was skipped and also what a
        // caller relies on to tell the two states apart.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await TalkAsync(broker, token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.Null(export.Destroyed);
    }

    [Fact]
    public async Task Exporting_the_same_conversation_twice_gives_the_same_answer()
    {
        // Nothing about the new field is consumed by reading, and nothing about the old fields
        // changed because of it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await TalkAsync(broker, token);

        var once = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        var again = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.Equal(once.Messages.Count, again.Messages.Count);
        Assert.Equal(once.Exchanges.Count, again.Exchanges.Count);
        Assert.Equal(once.Destroyed, again.Destroyed);
    }

    [Fact]
    public async Task An_export_of_an_empty_identifier_is_three_answers_apart_from_the_others()
    {
        // The distinction the field exists for, stated as the three cases side by side so that
        // collapsing any two of them fails here.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await TalkAsync(broker, token);

        var alive = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        var never = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "never-existed" }, cancellationToken: token);

        await broker.ConnectAsOperator().DestroyAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        var gone = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.NotEmpty(alive.Messages);
        Assert.Null(alive.Destroyed);

        Assert.Empty(never.Messages);
        Assert.Null(never.Destroyed);

        Assert.Empty(gone.Messages);
        Assert.NotNull(gone.Destroyed);
    }

    [Fact]
    public async Task Destroying_a_conversation_twice_leaves_one_account()
    {
        // The second destroy has nothing to destroy, and must not overwrite or duplicate the first
        // record -- which is the only account of those rows that will ever exist.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await TalkAsync(broker, token);

        var first = await broker.ConnectAsOperator().DestroyAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        var second = await broker.ConnectAsOperator().DestroyAsync(
            new Wire.ConversationRequest { ConversationId = "a-discussion" },
            cancellationToken: token);

        Assert.Equal(Wire.DestroyVerdict.Gone, first.Verdict);
        Assert.Equal(Wire.DestroyVerdict.NoSuchConversation, second.Verdict);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "a-discussion" }, cancellationToken: token);

        Assert.NotNull(export.Destroyed);
        Assert.Equal(1, export.Destroyed.Messages);
    }
}
