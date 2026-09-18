using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// One line of work from first Request to closed by consensus, across a restart.
/// </summary>
/// <remarks>
/// Every other test here isolates a rule. This one runs the whole of SPEC.md §10.1 as a participant would meet
/// it — request, collect, clarify, result, conclude — with an agent's session ending in the middle, because the
/// parts have been correct individually while the joins between them were where defects lived.
/// </remarks>
public sealed class FullDiscussionTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = Requests.Model;
    private const string Conversation = "pricing";

    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;

    public FullDiscussionTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing, Inventory);
        _conversations = new ConversationRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_line_of_work_runs_from_first_request_to_closed()
    {
        var ct = TestContext.Current.CancellationToken;

        var broker = new Broker(_connection);
        await broker.StartAsync(Orders, T0, ct);

        // Orders asks billing for work, naming the conversation.
        var sent = await broker.RequestAsync(
            Orders, Billing, Model, Requests.Request("state the price as gross or net"), T0.AddMinutes(1), Conversation, ct);
        Assert.True(sent.Succeeded, $"expected admission, got {sent.Rejection}");

        // Billing collects it, and needs detail, so the turn goes back.
        Assert.Equal(MessageType.Request, Assert.Single((await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: ct)).Messages).Message.Type);
        Assert.True((await broker.ClarifyAsync(sent.Exchange!.Id, Billing, Model, Requests.Clarify("gross of VAT or of duty?"), T0.AddMinutes(3), ct)).Succeeded);

        // Orders reads what it was asked for, and its session ends before it replies.
        Assert.Equal(MessageType.Clarify, Assert.Single((await broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: ct)).Messages).Message.Type);
        await broker.DetachAsync(ct);

        // Two hours later orders comes back. Nothing ended the exchange meanwhile, and the clarification it owes a
        // reply to is handed over again rather than left stamped as collected.
        var back = T0.AddHours(2);
        var restarted = new Broker(_connection);
        Assert.Equal(1, (await restarted.StartAsync(Orders, back, ct)).ExchangesRedelivered);
        Assert.Equal(MessageType.Clarify, Assert.Single((await restarted.DrainInboxAsync(Orders, back.AddMinutes(1), cancellationToken: ct)).Messages).Message.Type);

        Assert.True((await restarted.ClarifyAsync(sent.Exchange.Id, Orders, Model, Requests.Clarify("of VAT."), back.AddMinutes(2), ct)).Succeeded);

        // Billing does the work and sends the Result, which reaches orders with its own Request beside it.
        Assert.True((await restarted.ResultAsync(sent.Exchange.Id, Billing, Model, Requests.Result((PayloadShape.WholeRequest, "done")), back.AddMinutes(3), ct)).Succeeded);

        var result = Assert.Single((await restarted.DrainInboxAsync(Orders, back.AddMinutes(4), cancellationToken: ct)).Messages);
        Assert.Equal(MessageType.Result, result.Message.Type);
        Assert.Contains("state the price as gross or net", result.Envelope, StringComparison.Ordinal);

        // Orders says it is finished. Nobody holds open work any more, so nobody is asked, and the conversation
        // closes by consensus on the one statement.
        Assert.True((await restarted.ConcludeAsync(Conversation, Orders, true, back.AddMinutes(5), ct)).Concluded);

        // It stays closed: further work naming it is refused, with who closed it.
        var reopened = await restarted.RequestAsync(Orders, Inventory, Model, Requests.Request("and for refunds?"), back.AddMinutes(7), Conversation, ct);
        Assert.Equal(RequestRejection.ConversationClosed, reopened.Rejection);
        Assert.Equal(ConversationCloser.Consensus, reopened.Admission.ClosedBy);

        // The same work in a new conversation is admitted, which is what makes the refusal actionable.
        var afresh = await restarted.RequestAsync(Orders, Inventory, Model, Requests.Request("and for refunds?"), back.AddMinutes(8), "refunds", ct);
        Assert.True(afresh.Succeeded, $"expected admission, got {afresh.Rejection}");

        var record = await _conversations.GetAsync(Conversation, cancellationToken: ct);
        Assert.Equal(ConversationState.Closed, record!.State);
        Assert.Equal(back.AddMinutes(5), record.ClosedUtc);
    }
}
