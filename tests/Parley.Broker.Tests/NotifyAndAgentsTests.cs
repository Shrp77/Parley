using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// The two operations of SPEC.md §10.1 that need no exchange: <c>notify</c> and <c>agents</c>.
/// </summary>
/// <remarks>
/// <para>
/// A notification is the odd one out. §5.4 makes it fire-and-forget in a stronger sense than
/// everything else — nothing is owed in return — so it takes no pair slot, adds no wait-for
/// edge, and has none of the guards the other three lean on. §13.1 says as much: deduplication
/// is the only thing between an at-least-once transport and the same event arriving twice.
/// </para>
/// <para>
/// <c>agents()</c> is the only operation that tells an agent anything about another, which makes
/// what it withholds as much the subject as what it returns.
/// </para>
/// </remarks>
public sealed class NotifyAndAgentsTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";
    private const string Event = """{"text":"OrderLine gained a PriceSnapshot field."}""";

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly Broker _broker;

    public NotifyAndAgentsTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "test-adapter", AgentTier.Full,
                    Workspace: "/home/someone/secret-repo",
                    SessionRef: "session-abc123",
                    AdapterConfig: """{"endpoint":"https://internal.example/v1"}""",
                    LastSeenUtc: null,
                    Status: AgentStatus.Offline),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private Task<NotifyOutcome> Notify(
        AgentName? from = null,
        AgentName? to = null,
        string payload = Event,
        DateTimeOffset? at = null,
        string? conversationId = null) =>
        _broker.NotifyAsync(
            from ?? Orders, to ?? Billing, Model, payload, at ?? T0.AddMinutes(1),
            conversationId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_notification_reaches_the_inbox_and_owes_nothing()
    {
        var sent = await Notify();

        Assert.True(sent.Succeeded);
        Assert.True(sent.Stored);
        Assert.Null(sent.Message!.ExchangeId);

        var inbox = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(MessageType.Notify, Assert.Single(inbox.Messages).Message.Type);

        // No exchange means no debt, so nothing is open and nothing will ever expire.
        Assert.Empty(await new ExchangeRepository(_connection).GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Notifying_takes_no_pair_slot()
    {
        // §5.4: it opens no exchange and takes no slot. Enough notifications to blow past the
        // §7.1 cap several times over, and a question afterwards is still admitted.
        for (var i = 0; i < ExchangePolicy.Default.OpenPairCap * 3; i++)
        {
            var sent = await Notify(payload: $$"""{"text":"event {{i}}"}""");
            Assert.True(sent.Succeeded);
        }

        var asked = await _broker.AskAsync(
            Orders, Billing, Model,
            """{"text":"gross or net?","consequences":"If gross I stop adding tax; if net I keep it."}""",
            T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");
    }

    [Fact]
    public async Task A_repeated_notification_inside_the_window_is_suppressed()
    {
        // The one guard it has. §13.1: a repeated question is refused by the pair slot, a
        // repeated clarify by the turn check, a repeated answer by the exchange no longer being
        // open — and Notify by nothing else at all.
        var first = await Notify();
        var retry = await Notify(at: T0.AddMinutes(1).AddSeconds(5));

        Assert.True(first.Stored);
        Assert.False(retry.Stored);
        Assert.Equal(first.Message!.Id, retry.Message!.Id);

        var inbox = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(inbox.Messages);
    }

    [Fact]
    public async Task The_same_event_outside_the_window_is_a_second_event()
    {
        await Notify();
        var later = await Notify(at: T0.AddMinutes(1) + MessageIdentity.DeduplicationWindow.Add(TimeSpan.FromSeconds(5)));

        Assert.True(later.Stored);
    }

    [Fact]
    public async Task A_notification_to_an_unroutable_peer_is_refused()
    {
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);
        Assert.Equal(NotifyRejection.RecipientUnroutable, (await Notify()).Rejection);

        // Silence deliberately does not refuse: an agent nobody has open is the ordinary case,
        // and a notification is one of the things that gives a supervisor a reason to start it.
        GoSilent(Inventory);
        Assert.True((await Notify(to: Inventory)).Succeeded);
    }

    [Fact]
    public async Task An_adapter_configured_with_an_unregistered_identity_is_told_so()
    {
        // A deployment fault rather than an agent one, which is why it throws where an
        // unroutable peer returns a refusal: an agent cannot register itself, so there is nothing
        // for it to act on. Found by probe — it was previously a foreign-key violation from three
        // layers down, arriving on whichever operation happened to run first.
        var ghost = AgentName.Create("ghost");

        var notifying = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _broker.NotifyAsync(
                ghost, Billing, Model, Event, T0.AddMinutes(1),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("ghost", notifying.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _broker.AskAsync(
                ghost, Billing, Model,
                """{"text":"gross or net?","consequences":"If gross I stop adding tax; if net I keep it."}""",
                T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refused_notification_leaves_no_conversation_behind()
    {
        // The checks and the write share one transaction, so a refusal cannot mint the discussion
        // it was refused from. Without that, an agent could open conversations by announcing into
        // them badly — the notify equivalent of the phantom exchange.
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);

        var sent = await Notify(conversationId: "invented");
        Assert.Equal(NotifyRejection.RecipientUnroutable, sent.Rejection);

        Assert.Null(await new ConversationRepository(_connection).GetAsync(
            "invented", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_recipient_is_refused_rather_than_thrown()
    {
        // Found by probe. A name that is not registered reached the INSERT and came back as a
        // foreign-key violation, which is a raw storage exception for an ordinary mistake — a
        // typo, or a name remembered from before a re-registration. §7.3 asks for a refusal the
        // caller can act on, and agents() is what it should act on.
        var sent = await Notify(to: AgentName.Create("ghost"));

        Assert.Equal(NotifyRejection.RecipientUnroutable, sent.Rejection);
    }

    [Fact]
    public async Task An_unknown_name_and_a_retired_one_are_refused_identically()
    {
        // §14 keeps retired agents out of agents(). Answering differently for a retired name
        // than for one that never existed would hand that state straight back, and the two mean
        // the same thing to a sender anyway: a name it cannot address.
        var unknown = await Notify(to: AgentName.Create("ghost"));

        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);
        var retired = await Notify(to: Billing);

        Assert.Equal(unknown.Rejection, retired.Rejection);
    }

    [Fact]
    public async Task An_unknown_answerer_is_refused_rather_than_thrown()
    {
        // The same defect on the question path, which shares the predicate.
        var asked = await _broker.AskAsync(
            Orders, AgentName.Create("ghost"), Model,
            """{"text":"gross or net?","consequences":"If gross I stop adding tax; if net I keep it."}""",
            T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.AnswererUnreachable, asked.Rejection);
    }

    [Fact]
    public async Task A_retired_agent_may_not_announce()
    {
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        Assert.Equal(NotifyRejection.SenderRetired, (await Notify()).Rejection);
    }

    [Fact]
    public async Task A_notification_may_not_add_to_a_conversation_that_concluded()
    {
        // Found by probe, as an asymmetry rather than an outright hole: the question naming a
        // concluded discussion was refused and the notification beside it was admitted. §5.0
        // words its refusal in terms of a Question, because that is what takes a slot and grows
        // membership — but the hazard it names is a message left "in a closed discussion nobody
        // may add to", and a notification does that just as well.
        var asked = await _broker.AskAsync(
            Orders, Billing, Model,
            """{"text":"gross or net?","consequences":"If gross I stop adding tax; if net I keep it."}""",
            T0.AddMinutes(1), "pricing", TestContext.Current.CancellationToken);
        Assert.True(asked.Succeeded);

        await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model, """{"resolutions":[]}""", T0.AddMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);
        await _broker.ConcludeAsync(
            "pricing", Orders, true, T0.AddMinutes(3), TestContext.Current.CancellationToken);

        var sent = await Notify(at: T0.AddMinutes(4), conversationId: "pricing");

        Assert.Equal(NotifyRejection.ConversationClosed, sent.Rejection);
    }

    [Fact]
    public async Task A_notification_declares_its_conversation_without_joining_it()
    {
        // §5.0: a Notify has no exchange, so it declares its own conversation rather than
        // inheriting one. Declaring means the discussion exists — a message naming one that does
        // not would leave export reporting on something the registry never heard of.
        //
        // It confers no membership, though, and that is the load-bearing half. Membership is
        // derived from being party to an exchange, and a notification is not one; if announcing
        // an event made the announcer a participant, it would also give it a vote in a closure
        // poll it has no stake in.
        var sent = await Notify(conversationId: "shipping");
        Assert.True(sent.Succeeded);
        Assert.Equal("shipping", sent.Message!.ConversationId);

        var conversations = new ConversationRepository(_connection);
        var record = await conversations.GetAsync(
            "shipping", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ConversationState.Open, record!.State);
        Assert.False(await conversations.IsParticipantAsync(
            "shipping", Orders, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await conversations.IsParticipantAsync(
            "shipping", Billing, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_notification_already_written_still_reaches_a_retired_recipient()
    {
        // Consistent with an exchange opened before retirement finishing: retirement stops new
        // routing, and work already written is not discarded. The refusal above is what stops
        // more accumulating.
        await Notify();
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);

        var inbox = await _broker.DrainInboxAsync(
            Billing, T0.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(inbox.Messages);
    }

    [Fact]
    public async Task A_notification_may_not_claim_the_broker_identity()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _broker.NotifyAsync(
            Orders, Billing, "parley:broker", Event, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Notifying_before_start_is_refused()
    {
        var broker = new Broker(_connection, BrokerOptions.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.NotifyAsync(
            Orders, Billing, Model, Event, T0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Listing_agents_says_who_can_be_asked_and_nothing_more()
    {
        var listed = await _broker.ListAgentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, listed.Count);

        var billing = Assert.Single(listed, a => a.Name == Billing);
        Assert.Equal("test-adapter", billing.Adapter);
        Assert.Equal(AgentTier.Full, billing.Tier);

        // The registry also holds a workspace path, a session handle and the adapter's own
        // configuration. None of them has anywhere to go in this type, which is the point:
        // rule 3 says never describe your internals, and this is the one operation whose job is
        // to describe agents.
        var exposed = typeof(AgentSummary).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Workspace", exposed);
        Assert.DoesNotContain("SessionRef", exposed);
        Assert.DoesNotContain("AdapterConfig", exposed);
    }

    [Fact]
    public async Task An_unreachable_agent_is_listed_and_a_retired_one_is_not()
    {
        // §14 treats these differently on purpose. Unreachable is visible so a peer can stop
        // asking rather than infer it from silence; retired is absent, because listing an agent
        // that admission will refuse teaches the asker nothing and costs it a turn.
        GoSilent(Billing);
        await _agents.RetireAsync(Inventory, TestContext.Current.CancellationToken);

        var listed = await _broker.ListAgentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Unreachable, Assert.Single(listed, a => a.Name == Billing).Status);
        // Derived from silence rather than read from a column: nothing wrote that status.
        Assert.DoesNotContain(listed, a => a.Name == Inventory);
    }

    [Fact]
    public async Task A_reinstated_agent_is_listed_again()
    {
        await _agents.RetireAsync(Inventory, TestContext.Current.CancellationToken);
        await _agents.ReinstateAsync(Inventory, TestContext.Current.CancellationToken);

        var listed = await _broker.ListAgentsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(listed, a => a.Name == Inventory);
    }

    [Fact]
    public async Task Listing_agents_before_start_is_refused()
    {
        // The liveness it reports is exactly what the reboot sequence invalidates: before it
        // runs, every agent still carries whatever the previous run last observed.
        var broker = new Broker(_connection, BrokerOptions.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.ListAgentsAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Ages an agent's heartbeat until it reads as unreachable.</summary>
    /// <param name="agent">The agent to silence.</param>
    /// <remarks>
    /// Unreachable is a reading of <c>last_seen_utc</c> now rather than a status anybody writes,
    /// so this is the only way a deployment can produce one: the agent stops checking in. There
    /// is deliberately no method to call instead — a test that could write the status directly
    /// would be testing a path the product does not have.
    /// </remarks>
    private void GoSilent(AgentName agent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE agents SET status = 'live', last_seen_utc = @Then WHERE name = @Name;";
        command.Parameters.AddWithValue(
            "@Then", DateTimeOffset.UtcNow.AddHours(-1).ToString("o"));
        command.Parameters.AddWithValue("@Name", agent.Value);
        command.ExecuteNonQuery();
    }

}
