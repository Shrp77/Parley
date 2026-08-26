using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// One discussion from first question to concluded, across a restart.
/// </summary>
/// <remarks>
/// Every other test here isolates a rule. This one runs the whole of SPEC.md §10.1 as an agent
/// would meet it — ask, drain, clarify, answer, conclude — with §14's reboot sequence in the
/// middle, because the parts have been correct individually while the joins between them were
/// where the last several defects lived: a replay that handed nothing back, a poll that a
/// restart let one participant win alone.
/// </remarks>
public sealed class FullDiscussionTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";
    private const string Conversation = "pricing";

    private readonly SqliteConnection _connection;
    private readonly ConversationRepository _conversations;

    public FullDiscussionTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Offline),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _conversations = new ConversationRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static string Question(string text) =>
        $$"""{"text":"{{text}}","consequences":"If gross I stop adding tax; if net I keep adding it."}""";

    [Fact]
    public async Task A_discussion_runs_from_first_question_to_concluded()
    {
        var ct = TestContext.Current.CancellationToken;

        // Always, so the restart in the middle replays rather than abandoning.
        var options = BrokerOptions.Default with
        {
            ResumePolicy = ResumePolicy.Always,
            ResumeGate = AlwaysResume.Instance
        };

        var broker = new Broker(_connection, options);
        await broker.StartAsync(Orders, T0, ct);

        // Orders asks billing, naming the discussion.
        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question("is the price gross or net?"),
            T0.AddMinutes(1), Conversation, ct);
        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");

        // Billing collects it, which starts the working window.
        var billingInbox = await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: ct);
        Assert.Equal(MessageType.Question, Assert.Single(billingInbox.Messages).Message.Type);

        // It needs detail, so the turn goes back.
        Assert.True((await broker.ClarifyAsync(
            asked.Exchange!.Id, Billing, Model, """{"text":"gross of VAT or of duty?"}""",
            T0.AddMinutes(3), ct)).Succeeded);

        // Orders reads what it was asked for, and then gets no further with it.
        Assert.Equal(MessageType.Clarify, Assert.Single(
            (await broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: ct))
                .Messages).Message.Type);

        // The process dies here, mid-clarification, with orders holding the turn — and holding
        // it knowingly, which is what makes the debt one the attach sequence releases. Had orders
        // never collected the clarification, §14 would leave the exchange open for it to find.
        await broker.DetachAsync(ct);

        // Two hours later, which is what a restart looks like from the registry: the previous
        // session stopped renewing its liveness lease, and the lease lapsing is the evidence
        // that nobody is still holding this. Coming back inside the lease would find the agent
        // apparently still attached and release nothing, which is the right answer for a second
        // window opened beside a live session and the wrong one for a process that died.
        var back = T0.AddHours(2);
        var restarted = new Broker(_connection, options);
        var reboot = await restarted.StartAsync(Orders, back, ct);
        Assert.Equal(1, reboot.ExchangesOrphaned);

        // Orders comes back. Its own traffic is what replays what it owes, and the question it
        // owes an answer to is handed over again rather than left stamped as delivered.
        var ordersInbox = await restarted.DrainInboxAsync(Orders, back.AddMinutes(1), cancellationToken: ct);
        Assert.Equal(MessageType.Clarify, Assert.Single(ordersInbox.Messages).Message.Type);

        Assert.True((await restarted.ClarifyAsync(
            asked.Exchange.Id, Orders, Model, """{"text":"of VAT."}""", back.AddMinutes(2), ct)).Succeeded);

        // Billing answers, closing the exchange and releasing the pair slot.
        var answered = await restarted.AnswerAsync(
            asked.Exchange.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            back.AddMinutes(3), Resolution.Answered, isDecision: true, cancellationToken: ct);
        Assert.True(answered.Succeeded);

        var settled = await restarted.DrainInboxAsync(Orders, back.AddMinutes(4), cancellationToken: ct);
        Assert.Equal(MessageType.Answer, Assert.Single(settled.Messages).Message.Type);

        // Orders proposes closure. Billing holds nothing open now, so it is not woken to answer
        // a question about nothing, and the discussion concludes on the proposal alone.
        var concluded = await restarted.ConcludeAsync(Conversation, Orders, true, back.AddMinutes(5), ct);
        Assert.True(concluded.Concluded);

        // And it stays concluded: a further question naming it is refused, with a reason.
        var reopened = await restarted.AskAsync(
            Orders, Inventory, Model, Question("and for refunds?"),
            back.AddMinutes(6), Conversation, ct);
        Assert.Equal(OpenRejection.ConversationClosed, reopened.Rejection);

        // The same question under a new identifier is admitted, which is what makes that refusal
        // something an agent can act on rather than a dead end.
        var afresh = await restarted.AskAsync(
            Orders, Inventory, Model, Question("and for refunds?"),
            back.AddMinutes(7), "refunds", ct);
        Assert.True(afresh.Succeeded, $"expected admission, got {afresh.Rejection}");

        var record = await _conversations.GetAsync(Conversation, cancellationToken: ct);
        Assert.Equal(ConversationState.Closed, record!.State);
        Assert.Equal(back.AddMinutes(5), record.ClosedUtc);
    }
}
