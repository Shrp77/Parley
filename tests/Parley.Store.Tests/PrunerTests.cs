using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The retention policy of SPEC.md §9.4, and the floor that bounds it.
/// </summary>
/// <remarks>
/// <para>
/// Written against the two failure directions rather than the happy path, because both are
/// silent. Pruning too much removes an inbox nobody knew was there; pruning too little leaves
/// the unbounded log §9.4 exists to prevent, and neither reports anything. The counts are the
/// only evidence either way, so most of these assert on what survived rather than on what went.
/// </para>
/// </remarks>
public sealed class PrunerTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Question =
        """{"text":"is the invoice total gross or net?","consequences":"If gross I stop adding tax at invoice time; if net I keep it."}""";

    private const string Model = "some-model-9";

    private static readonly RetentionPolicy Policy =
        new(IdlePeriod: TimeSpan.FromDays(3), MaximumAge: TimeSpan.FromDays(30));

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly ConversationRepository _conversations;
    private readonly Pruner _pruner;

    public PrunerTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
        _conversations = new ConversationRepository(_connection);
        _pruner = new Pruner(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Exchange> Open(
        DateTimeOffset at, string hash, string? conversationId = null)
    {
        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, at, hash, conversationId: conversationId, cancellationToken: Ct);

        Assert.NotNull(outcome.Exchange);
        return outcome.Exchange;
    }

    private async Task<long> Say(
        long? exchangeId,
        DateTimeOffset at,
        MessageType type = MessageType.Question,
        string? conversationId = null,
        bool delivered = true,
        string payload = Question)
    {
        var appended = await _messages.AppendAsync(
            exchangeId, type, Orders, Billing, Model, payload, at, conversationId,
            cancellationToken: Ct);

        Assert.True(appended.Stored);

        if (delivered)
        {
            Assert.True(await _messages.MarkDeliveredAsync(appended.Message.Id, at, Ct));
        }

        return appended.Message.Id;
    }

    /// <summary>Closes an exchange the way an answer does, so the floor stops applying.</summary>
    private async Task Settle(Exchange exchange, DateTimeOffset at)
    {
        Assert.True(exchange.Deliver(at).Succeeded);
        Assert.True(exchange.Answer(Billing, at).Succeeded);
        Assert.True(await _exchanges.SaveAsync(exchange, cancellationToken: Ct));
    }

    private async Task<long> MessageCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<PruneResult> Prune(DateTimeOffset at, RetentionPolicy? policy = null) =>
        _pruner.PruneAsync(policy ?? Policy, at, Ct);

    // ---- the idle rule ----

    [Fact]
    public async Task A_finished_discussion_that_has_gone_quiet_is_pruned()
    {
        var exchange = await Open(T0, "q1", "pricing");
        await Say(exchange.Id, T0, conversationId: "pricing");
        await Settle(exchange, T0.AddMinutes(5));

        // Inside the idle period, nothing goes — and nothing is held either. The floor and the
        // rules are different reasons to keep a message, and a run that reported this one as
        // "held" would be saying the discussion was over and something was in the way.
        var early = await Prune(T0.AddDays(2));

        Assert.Equal(0, early.Removed);
        Assert.Equal(0, early.HeldByFloor);
        Assert.Equal(1, await MessageCount());

        // Past it, the discussion is over.
        Assert.Equal(1, (await Prune(T0.AddDays(4))).Removed);
        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task One_exchange_still_running_keeps_the_whole_conversation()
    {
        // The first half of the idle rule, and the reason §9.2 puts conversation_id on
        // exchanges: "every exchange in a conversation is closed" cannot be evaluated from the
        // traffic alone, and a conversation that is still being worked is not idle merely
        // because its earliest thread went quiet.
        var settled = await Open(T0, "q1", "pricing");
        await Say(settled.Id, T0, conversationId: "pricing");
        await Settle(settled, T0.AddMinutes(5));

        // A second exchange in the same discussion, opened much later and never answered. Its
        // own messages are floor-protected; the question is whether the first one's are.
        var running = await Open(T0.AddDays(10), "q2", "pricing");
        await Say(running.Id, T0.AddDays(10), conversationId: "pricing");

        var result = await Prune(T0.AddDays(20));

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, await MessageCount());
    }

    [Fact]
    public async Task An_exchange_with_no_conversation_is_a_conversation_of_one()
    {
        var alone = await Open(T0, "q1");
        await Say(alone.Id, T0);
        await Settle(alone, T0.AddMinutes(5));

        var other = await Open(T0, "q2", "pricing");
        await Say(other.Id, T0, conversationId: "pricing");

        // The standalone exchange is finished and quiet; the conversation is not. Grouping that
        // treated "no conversation" as one big bucket would let either hold the other open.
        var result = await Prune(T0.AddDays(4));

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, await MessageCount());
    }

    [Fact]
    public async Task A_notification_carries_its_own_group()
    {
        // A notify names no exchange at all, so neither half of the idle rule has anything to
        // read. It is a conversation of one message, judged on its own age.
        await Say(null, T0, MessageType.Notify, payload: """{"text":"deploy finished"}""");

        Assert.Equal(0, (await Prune(T0.AddDays(2))).Removed);
        Assert.Equal(1, (await Prune(T0.AddDays(4))).Removed);
    }

    [Fact]
    public async Task A_message_carrying_no_conversation_is_grouped_by_its_exchange_s()
    {
        // The composite foreign key of §9.2 is satisfied by a NULL in either column, so the
        // schema admits a message that carries no conversation while its exchange does — and
        // says so in the column comment, calling the copy something that "can fail to help; it
        // cannot lie". Grouping by the copy would put such a message in a group of its own and
        // prune it out from under a discussion that is still running.
        //
        // Inserted directly rather than through MessageRepository, which reads the conversation
        // from the exchange and would fill it in. That is the repository holding an invariant
        // the schema does not, and a pruner resting on it would be resting on the wrong one: the
        // grouping has to be right for every row storage will accept, not only for the rows this
        // one writer produces. Written this way the first attempt at this test proved nothing —
        // it asserted a case it had not managed to create.
        var settled = await Open(T0, "q1", "pricing");
        await InsertWithoutConversation(settled.Id, T0);
        await Settle(settled, T0.AddMinutes(5));

        var running = await Open(T0.AddDays(10), "q2", "pricing");
        await Say(running.Id, T0.AddDays(10), conversationId: "pricing");

        Assert.Equal(0, (await Prune(T0.AddDays(20))).Removed);
        Assert.Equal(2, await MessageCount());
    }

    /// <summary>
    /// Writes a delivered question whose conversation column is NULL although its exchange's
    /// is not.
    /// </summary>
    /// <param name="exchangeId">The exchange to attach it to.</param>
    /// <param name="at">When it was written and delivered.</param>
    private async Task InsertWithoutConversation(long exchangeId, DateTimeOffset at)
    {
        var stamp = ExchangeRow.FormatTimestamp(at);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc, delivered_utc)
            VALUES
                ($exchange, 'question', $from, $to, $model, $payload, $hash, NULL, $at, $at);
            """;

        command.Parameters.AddWithValue("$exchange", exchangeId);
        command.Parameters.AddWithValue("$from", Orders.Value);
        command.Parameters.AddWithValue("$to", Billing.Value);
        command.Parameters.AddWithValue("$model", Model);
        command.Parameters.AddWithValue("$payload", Question);
        command.Parameters.AddWithValue(
            "$hash", MessageIdentity.Compute(
                exchangeId, MessageType.Question, Orders, Billing, Question));
        command.Parameters.AddWithValue("$at", stamp);

        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task Later_traffic_in_the_same_discussion_keeps_the_earlier_messages()
    {
        // The second half of the idle rule, and the half a per-message age check would miss.
        // "Nothing has been added to it" is a property of the conversation, not of the row: an
        // old message in a discussion somebody returned to last night is not idle, and pruning
        // it would leave the export of §10.2 showing a reply to a question that is no longer
        // there.
        var first = await Open(T0, "q1", "pricing");
        await Say(first.Id, T0, conversationId: "pricing");
        await Settle(first, T0.AddMinutes(5));

        var second = await Open(T0.AddDays(10), "q2", "pricing");
        await Say(second.Id, T0.AddDays(10), conversationId: "pricing");
        await Settle(second, T0.AddDays(10).AddMinutes(5));

        // Twelve days on: the first message is nine days past the idle period on its own, and
        // the discussion it belongs to is two.
        Assert.Equal(0, (await Prune(T0.AddDays(12))).Removed);
        Assert.Equal(2, await MessageCount());

        // Once the discussion itself has been quiet for the idle period, both go together.
        Assert.Equal(2, (await Prune(T0.AddDays(14))).Removed);
    }

    [Fact]
    public async Task Concluding_a_discussion_does_not_wait_out_the_idle_period()
    {
        var exchange = await Open(T0, "q1", "pricing");
        await Say(exchange.Id, T0, conversationId: "pricing");
        await Settle(exchange, T0.AddMinutes(5));

        // Well inside the idle period, and gone anyway: the participants have said the
        // discussion is over, which is better evidence than silence and arrives sooner.
        Assert.True(await _conversations.ForceConcludeAsync("pricing", T0.AddMinutes(10), cancellationToken: Ct));

        Assert.Equal(1, (await Prune(T0.AddMinutes(20))).Removed);
    }

    // ---- the floor ----

    [Fact]
    public async Task An_exchange_that_is_still_open_keeps_its_question_past_the_age_cap()
    {
        // The clause that must stay absolute. §14 may still replay this, and the question text
        // is what there would be to replay — so an operator with an aggressive cap must not be
        // able to delete a live exchange out from under the machinery.
        var exchange = await Open(T0, "q1");
        await Say(exchange.Id, T0);

        var aggressive = new RetentionPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
        var result = await Prune(T0.AddDays(365), aggressive);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.HeldByFloor);
        Assert.Equal(1, await MessageCount());
    }

    [Fact]
    public async Task An_orphan_keeps_its_question_too()
    {
        var exchange = await Open(T0, "q1");
        await Say(exchange.Id, T0);

        Assert.True(exchange.Orphan(T0.AddHours(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(exchange, cancellationToken: Ct));

        // Bounded on its own, which is why it needs no override: §14 ghosts an orphan once its
        // grace period elapses, and a closed exchange is subject to the ordinary rules.
        Assert.Equal(0, (await Prune(T0.AddDays(365))).Removed);
    }

    [Fact]
    public async Task An_undelivered_message_survives_the_idle_rule()
    {
        var exchange = await Open(T0, "q1", "pricing");
        await Say(exchange.Id, T0, conversationId: "pricing", delivered: false);
        await Settle(exchange, T0.AddMinutes(5));

        var result = await Prune(T0.AddDays(4));

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.HeldByFloor);
    }

    [Fact]
    public async Task An_undelivered_message_does_not_survive_the_age_cap()
    {
        // The clause with no terminal state. An agent that never attaches never collects, and
        // nothing else clears an inbox nobody reads — so if the cap did not reach this, the one
        // unbounded corner of the log would be the part carrying the full text of questions.
        var exchange = await Open(T0, "q1", "pricing");
        await Say(exchange.Id, T0, conversationId: "pricing", delivered: false);
        await Settle(exchange, T0.AddMinutes(5));

        var result = await Prune(T0.AddDays(31));

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.HeldByFloor);
        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task A_message_inside_the_deduplication_window_is_kept()
    {
        // Not a hypothetical: the row is what the exact check of §13.1 reads, so removing it
        // would let a byte-identical message through. An operator tightening retention must not
        // be able to break deduplication as a side effect.
        var exchange = await Open(T0, "q1");
        await Say(exchange.Id, T0);
        await Settle(exchange, T0);

        var aggressive = new RetentionPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        // Ten seconds later: spent under a one-second policy, and inside the window regardless.
        var held = await Prune(T0.AddSeconds(10), aggressive);

        Assert.Equal(0, held.Removed);
        Assert.Equal(1, held.HeldByFloor);

        // And the mechanism it protects still works, which is the property rather than the row.
        var duplicate = await _messages.AppendAsync(
            exchange.Id, MessageType.Question, Orders, Billing, Model, Question,
            T0.AddSeconds(10), null, cancellationToken: Ct);

        Assert.False(duplicate.Stored);
    }

    [Fact]
    public async Task Past_the_window_the_same_message_goes()
    {
        // The other side of the previous test. Without this, "kept inside the window" would
        // pass just as well against a pruner that kept everything for ever.
        var exchange = await Open(T0, "q1");
        await Say(exchange.Id, T0);
        await Settle(exchange, T0);

        var aggressive = new RetentionPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var result = await Prune(T0.AddMinutes(5), aggressive);

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.HeldByFloor);
    }

    // ---- what is not the target ----

    [Fact]
    public async Task The_exchange_row_outlives_the_text_it_carried()
    {
        // §9.4: exchange rows carry no free prose and are what §15's telemetry is computed from,
        // so pruning the text while keeping the skeleton is what lets a deployment answer "how
        // often does this pair disagree" a year later.
        var exchange = await Open(T0, "q1", "pricing");
        await Say(exchange.Id, T0, conversationId: "pricing");
        await Settle(exchange, T0.AddMinutes(5));

        Assert.Equal(1, (await Prune(T0.AddDays(4))).Removed);

        var survivor = await _exchanges.GetAsync(exchange.Id, cancellationToken: Ct);

        Assert.NotNull(survivor);
        Assert.Equal(ExchangeState.Closed, survivor.State);
        Assert.Equal(Resolution.Answered, survivor.Resolution);

        // And the conversation, which an exchange still references.
        Assert.NotNull(await _conversations.GetAsync("pricing", cancellationToken: Ct));
    }

    [Fact]
    public async Task Pruning_an_empty_log_does_nothing_and_says_so()
    {
        var result = await Prune(T0.AddDays(400));

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.HeldByFloor);
    }

    [Fact]
    public async Task Message_identifiers_are_never_reused()
    {
        // What the pruner rests on. It scans for identifiers outside a transaction and deletes
        // by them afterwards, so an identifier reissued in between would let a decision made
        // about one message remove a different one. SQLite reissues the highest rowid on a plain
        // INTEGER PRIMARY KEY and never does under AUTOINCREMENT, so this is the schema keyword
        // the whole arrangement depends on — checked here rather than trusted, because a
        // migration that dropped it would break the pruner silently and nowhere near this file.
        var exchange = await Open(T0, "q1");
        var first = await Say(exchange.Id, T0);
        await Settle(exchange, T0.AddMinutes(5));

        Assert.Equal(1, (await Prune(T0.AddDays(4))).Removed);

        var second = await Open(T0.AddDays(5), "q2");
        var reused = await Say(second.Id, T0.AddDays(5));

        Assert.True(reused > first, $"message {first} was reissued as {reused}");
    }

    [Fact]
    public async Task An_unusable_policy_is_refused_rather_than_applied()
    {
        // Checked here as well as at broker construction, because this is the entry point the
        // operator's command reaches directly and a policy that arrived from an environment
        // variable has had no other opportunity to be judged.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _pruner.PruneAsync(
                new RetentionPolicy(TimeSpan.FromDays(30), TimeSpan.FromDays(3)), T0, Ct));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _pruner.PruneAsync(null!, T0, Ct));
    }
}
