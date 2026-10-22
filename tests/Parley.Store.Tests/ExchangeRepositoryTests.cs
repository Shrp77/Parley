using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

public sealed class ExchangeRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;

    public ExchangeRepositoryTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing, Inventory);
        _exchanges = new ExchangeRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Distinctness is keyed on the Request's content, so a test opening two exchanges on one pair has
    // to say they are different Requests. The hash is computed from the text at each call, so "q1" and
    // "q2" reading differently is visible where it matters.
    private Task<OpenOutcome> Open(
        AgentName from,
        AgentName to,
        string text = "q1",
        ExchangePolicy? policy = null,
        string conversation = Work.Conversation,
        DateTimeOffset? at = null) =>
        _exchanges.TryOpenAsync(
            from, to, at ?? T0, MessageIdentity.ComputeRequest(from, to, Work.Request(text)), conversation,
            policy, cancellationToken: Token);

    [Fact]
    public async Task Opening_persists_and_round_trips()
    {
        var outcome = await Open(Orders, Billing);
        Assert.True(outcome.Succeeded);

        var loaded = await _exchanges.GetAsync(outcome.Exchange!.Id, cancellationToken: Token);

        Assert.NotNull(loaded);
        Assert.Equal(Orders, loaded.Requester);
        Assert.Equal(Billing, loaded.Receiver);
        Assert.Equal(Billing, loaded.OwedBy);
        Assert.Equal(ExchangeState.Open, loaded.State);
        Assert.Equal(T0, loaded.OpenedUtc);
        Assert.Equal(ExchangePolicy.Default.UncollectedAfter, loaded.UncollectedAfter);
        Assert.Equal(ExchangePolicy.Default.StalledAfter, loaded.StalledAfter);
        Assert.Null(loaded.DeliveredUtc);
        Assert.Null(loaded.Outcome);
        Assert.Equal(0L, loaded.Version);
    }

    [Fact]
    public async Task Sub_second_thresholds_survive_the_round_trip()
    {
        // Milliseconds in storage, so a threshold a deployment set precisely comes back as set.
        var uncollected = TimeSpan.FromMilliseconds(1500);
        var stalled = TimeSpan.FromMilliseconds(2500);

        var outcome = await Open(
            Orders, Billing,
            policy: ExchangePolicy.Default with { UncollectedAfter = uncollected, StalledAfter = stalled });

        var loaded = await _exchanges.GetAsync(outcome.Exchange!.Id, cancellationToken: Token);

        Assert.Equal(uncollected, loaded!.UncollectedAfter);
        Assert.Equal(stalled, loaded.StalledAfter);
    }

    [Fact]
    public async Task A_policy_outside_what_storage_accepts_is_rejected()
    {
        // Checked here as well as in the schema, so a misconfigured deployment fails naming the field
        // rather than on its first Request with a constraint violation several layers below the mistake.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Open(Orders, Billing, policy: ExchangePolicy.Default with { UncollectedAfter = TimeSpan.Zero }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Open(Orders, Billing, policy: ExchangePolicy.Default with { OpenPairCap = 0 }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Open(Orders, Billing,
                policy: ExchangePolicy.Default with { OpenPairCap = ExchangePolicy.MaximumOpenPairCap + 1 }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Open(Orders, Billing, policy: ExchangePolicy.Default with { ClarifyDepthCap = -1 }));
    }

    [Fact]
    public async Task A_request_must_carry_its_content_identity_and_its_conversation()
    {
        // Distinctness is keyed on the hash, so an empty one would make every Request identical; and every
        // exchange belongs to a conversation (§5.0), so there is no exchange without one.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _exchanges.TryOpenAsync(Orders, Billing, T0, "  ", Work.Conversation, cancellationToken: Token));

        await Assert.ThrowsAsync<ArgumentException>(
            () => _exchanges.TryOpenAsync(Orders, Billing, T0, "hash", " ", cancellationToken: Token));
    }

    [Fact]
    public async Task A_request_to_oneself_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Open(Orders, Orders));
    }

    [Fact]
    public async Task A_reverse_request_is_admitted_and_its_cycle_recorded()
    {
        // Not a deadlock: nothing waits synchronously, and billing asking orders for
        // something while orders waits on billing is ordinary cross-repository work, so it is admitted —
        // and the loop is written down, where a loop that has stopped moving is visible to the person.
        await Open(Orders, Billing);

        var outcome = await Open(Billing, Orders, "q2");

        Assert.True(outcome.Succeeded);
        Assert.Equal([Billing, Orders], outcome.CyclePath);
        Assert.Equal("billing@test -> orders@test", Scalar<string>(
            "SELECT path FROM conversation_cycles WHERE exchange_id = @Id;", outcome.Exchange!.Id));
    }

    [Fact]
    public async Task A_three_cycle_is_admitted_with_its_path()
    {
        await Open(Orders, Billing);
        await Open(Billing, Inventory);

        var outcome = await Open(Inventory, Orders);

        Assert.True(outcome.Succeeded);
        Assert.Equal([Inventory, Orders, Billing], outcome.CyclePath);
    }

    [Fact]
    public async Task A_request_that_closes_no_cycle_records_none()
    {
        var outcome = await Open(Orders, Billing);

        Assert.Empty(outcome.CyclePath);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM conversation_cycles WHERE exchange_id = @Id;", outcome.Exchange!.Id));
    }

    [Fact]
    public async Task A_second_different_request_to_the_same_receiver_is_admitted()
    {
        await Open(Orders, Billing, "where does price live");

        var outcome = await Open(Orders, Billing, "who owns the discount ladder");

        Assert.True(outcome.Succeeded, $"expected admission, got {outcome.Rejection}");
    }

    [Fact]
    public async Task Repeating_a_live_request_is_refused_and_returns_the_exchange_carrying_it()
    {
        var first = await Open(Orders, Billing, "where does price live");

        var repeat = await Open(Orders, Billing, "where does price live", at: T0.AddMinutes(1));

        Assert.Equal(RequestRejection.Duplicate, repeat.Rejection);
        Assert.Equal(first.Exchange!.Id, Assert.Single(repeat.Outstanding).Id);
    }

    [Fact]
    public async Task A_request_repeated_after_its_exchange_closed_is_admitted()
    {
        // Distinctness holds only while the first is open. Asking again for work already reported on is a
        // new piece of work — the first answer may have been blocked on something since resolved.
        var first = await Open(Orders, Billing, "where does price live");
        await Work.CloseAsync(_connection, first.Exchange!, T0.AddMinutes(1), cancellationToken: Token);

        var again = await Open(Orders, Billing, "where does price live", at: T0.AddMinutes(2));

        Assert.True(again.Succeeded, $"expected admission, got {again.Rejection}");
    }

    [Fact]
    public async Task A_set_open_pair_cap_bounds_how_much_one_requester_may_have_in_flight()
    {
        var capped = ExchangePolicy.Default with { OpenPairCap = 3 };

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await Open(Orders, Billing, $"request {i}", capped)).Succeeded);
        }

        var refused = await Open(Orders, Billing, "one too many", capped);

        Assert.Equal(RequestRejection.Saturated, refused.Rejection);

        // The refusal hands back work rather than a retry instruction: a requester shown what it is already
        // waiting on can often see that this Request is covered by one of them.
        Assert.Equal(3, refused.Outstanding.Count);
    }

    [Fact]
    public async Task The_cap_is_directional()
    {
        var capped = ExchangePolicy.Default with { OpenPairCap = 1 };

        Assert.True((await Open(Orders, Billing, "only request", capped)).Succeeded);

        var reverse = await Open(Billing, Orders, "a different request", capped);

        Assert.True(reverse.Succeeded, $"expected admission, got {reverse.Rejection}");
    }

    [Fact]
    public async Task A_request_to_a_silent_agent_is_admitted_because_that_is_how_it_is_woken()
    {
        // Silence means only that nobody has that repository open, which is where most agents are most of
        // the time — and a Request is what gives a node a reason to start one.
        GoSilent(Billing);

        var outcome = await Open(Orders, Billing);

        Assert.True(outcome.Succeeded);
        Assert.Equal(Billing, outcome.Exchange!.OwedBy);
        Assert.Single(await _exchanges.GetOpenAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task An_offline_agent_is_still_routable()
    {
        Assert.Equal(1, await new AgentRepository(_connection).MarkOfflineAsync(Billing, Token));

        Assert.True((await Open(Orders, Billing)).Succeeded);
    }

    [Fact]
    public async Task An_unreachable_requester_may_still_send()
    {
        // The state describes whether the broker can reach an agent, not whether it may speak.
        GoSilent(Orders);

        Assert.True((await Open(Orders, Billing)).Succeeded);
    }

    [Fact]
    public async Task A_receiver_nothing_has_registered_is_refused()
    {
        var outcome = await Open(Orders, AgentName.Create("shipping@test"));

        Assert.Equal(RequestRejection.ReceiverNotRegistered, outcome.Rejection);
    }

    [Fact]
    public async Task Retirement_refuses_on_either_side()
    {
        // The one statement about a participant that refuses a Request (§14): the person has said this
        // agent is withdrawn, so neither work for it nor work from it is admitted.
        Retire(Billing);

        Assert.Equal(RequestRejection.ReceiverRetired, (await Open(Orders, Billing)).Rejection);
        Assert.Equal(RequestRejection.SenderRetired, (await Open(Billing, Orders)).Rejection);
    }

    [Fact]
    public async Task A_closed_conversation_admits_nothing_and_says_who_closed_it()
    {
        var conversations = new ConversationRepository(_connection);
        await conversations.EnsureOpenAsync("pricing-boundary", T0, cancellationToken: Token);
        Assert.True(await conversations.ForceConcludeAsync("pricing-boundary", "superseded", T0, cancellationToken: Token));

        var outcome = await Open(Orders, Billing, conversation: "pricing-boundary");

        Assert.Equal(RequestRejection.ConversationClosed, outcome.Rejection);
        Assert.Equal(ConversationCloser.Person, outcome.ClosedBy);
    }

    [Fact]
    public async Task Clarify_is_persisted()
    {
        var exchange = (await Open(Orders, Billing)).Exchange!;

        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(2)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(exchange, cancellationToken: Token));

        var loaded = await _exchanges.GetAsync(exchange.Id, cancellationToken: Token);

        Assert.Equal(Orders, loaded!.OwedBy);
        Assert.Equal(1, loaded.ClarifyDepth);
    }

    [Fact]
    public async Task Save_advances_the_version_stamp()
    {
        var exchange = (await Open(Orders, Billing)).Exchange!;
        Assert.Equal(0L, exchange.Version);

        exchange.Clarify(Billing, T0);
        Assert.True(await _exchanges.SaveAsync(exchange, cancellationToken: Token));
        Assert.Equal(1L, exchange.Version);

        // The instance must remain usable for a second save without reloading.
        exchange.Clarify(Orders, T0);
        Assert.True(await _exchanges.SaveAsync(exchange, cancellationToken: Token));
        Assert.Equal(2L, exchange.Version);
    }

    [Fact]
    public async Task A_stale_save_is_rejected_and_loses_no_clarify_depth()
    {
        var id = (await Open(Orders, Billing)).Exchange!.Id;

        // Two independent instances of the same row, as two broker code paths would hold.
        var a = await _exchanges.GetAsync(id, cancellationToken: Token);
        var b = await _exchanges.GetAsync(id, cancellationToken: Token);

        a!.Clarify(Billing, T0);
        Assert.True(await _exchanges.SaveAsync(a, cancellationToken: Token));

        b!.Clarify(Billing, T0);
        Assert.False(await _exchanges.SaveAsync(b, cancellationToken: Token));

        var reloaded = await _exchanges.GetAsync(id, cancellationToken: Token);
        Assert.Equal(1, reloaded!.ClarifyDepth);
        Assert.Equal(1L, reloaded.Version);
    }

    [Fact]
    public async Task Saving_a_missing_row_reports_failure()
    {
        Assert.False(await _exchanges.SaveAsync(Exchange.Open(999_999, Orders, Billing, T0), cancellationToken: Token));
    }

    [Fact]
    public async Task Storage_refuses_an_outcome_nobody_sent_a_result_for()
    {
        // The aggregate would close it; storage is what says a Result must exist first, so a code path
        // that forgot to write one cannot tell a requester its work is settled.
        var exchange = (await Open(Orders, Billing)).Exchange!;
        Assert.True(exchange.Close(Billing, T0, ExchangeOutcome.Complete).Succeeded);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => _exchanges.SaveAsync(exchange, cancellationToken: Token));

        Assert.Contains("closes on a result", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_keeps_a_closed_exchange_closed()
    {
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Token);
        await Work.CloseAsync(_connection, exchange, T0.AddMinutes(1), cancellationToken: Token);

        var reopened = Exchange.Rehydrate(
            exchange.Id, Orders, Billing, Billing, ExchangeState.Open, 0, exchange.ClarifyDepthCap,
            exchange.UncollectedAfter, exchange.StalledAfter, T0, null, null, null, exchange.Version);

        await Assert.ThrowsAsync<SqliteException>(() => _exchanges.SaveAsync(reopened, cancellationToken: Token));
    }

    // ---- which conversation a Request goes into (§5.0) ----

    [Fact]
    public async Task A_sender_holding_nothing_and_naming_nothing_starts_a_new_conversation()
    {
        var assigned = await _exchanges.AssignConversationAsync(Orders, null, senderIsPerson: false, cancellationToken: Token);

        Assert.True(assigned.Succeeded);
        Assert.True(assigned.Starts);
        Assert.StartsWith("c-", assigned.ConversationId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sender_holding_one_conversation_is_kept_in_it()
    {
        // The rule the whole assignment exists for: an agent working on a Request and asking somebody else
        // for part of it cannot split the line of work by naming nothing.
        await Open(Orders, Billing, conversation: "pricing-boundary");

        var assigned = await _exchanges.AssignConversationAsync(Billing, null, senderIsPerson: false, cancellationToken: Token);

        Assert.Equal("pricing-boundary", assigned.ConversationId);
        Assert.False(assigned.Starts);
    }

    [Fact]
    public async Task A_sender_holding_several_must_name_one_and_is_told_which()
    {
        await Open(Orders, Billing, conversation: "pricing-boundary");
        await Open(Inventory, Billing, conversation: "stock-levels");

        var assigned = await _exchanges.AssignConversationAsync(Billing, null, senderIsPerson: false, cancellationToken: Token);

        Assert.False(assigned.Succeeded);
        Assert.Equal(RequestRejection.ConversationNotAssignable, assigned.Refusal!.Rejection);
        Assert.Equal(["pricing-boundary", "stock-levels"], assigned.Refusal.Assignable);
    }

    [Fact]
    public async Task A_sender_holding_requests_cannot_start_a_new_conversation_by_naming_one()
    {
        await Open(Orders, Billing, conversation: "pricing-boundary");

        var assigned = await _exchanges.AssignConversationAsync(Billing, "somewhere-new", senderIsPerson: false, cancellationToken: Token);

        Assert.Equal(RequestRejection.ConversationNotAssignable, assigned.Refusal!.Rejection);
        Assert.Equal(["pricing-boundary"], assigned.Refusal.Assignable);
    }

    [Fact]
    public async Task Waiting_on_a_request_is_not_holding_one()
    {
        // Held means received. Orders waits on billing, and what orders does meanwhile is its own work.
        await Open(Orders, Billing, conversation: "pricing-boundary");

        var assigned = await _exchanges.AssignConversationAsync(Orders, "somewhere-new", senderIsPerson: false, cancellationToken: Token);

        Assert.Equal("somewhere-new", assigned.ConversationId);
        Assert.True(assigned.Starts);
    }

    [Fact]
    public async Task A_participant_holding_nothing_may_continue_its_conversation_and_a_stranger_may_not()
    {
        await Open(Orders, Billing, conversation: "pricing-boundary");

        var participant = await _exchanges.AssignConversationAsync(Orders, "pricing-boundary", senderIsPerson: false, cancellationToken: Token);
        var stranger = await _exchanges.AssignConversationAsync(Inventory, "pricing-boundary", senderIsPerson: false, cancellationToken: Token);

        Assert.True(participant.Succeeded);
        Assert.False(participant.Starts);
        Assert.Equal(RequestRejection.ConversationNotAssignable, stranger.Refusal!.Rejection);
    }

    [Fact]
    public async Task A_person_may_start_or_join_any_open_conversation()
    {
        // A person is where lines of work begin (principle 2.10), and the person running the deployment
        // may take part in any of them.
        await Open(Orders, Billing, conversation: "pricing-boundary");

        var joining = await _exchanges.AssignConversationAsync(Inventory, "pricing-boundary", senderIsPerson: true, cancellationToken: Token);

        Assert.Equal("pricing-boundary", joining.ConversationId);
    }

    [Fact]
    public async Task Naming_a_closed_conversation_is_refused_with_who_closed_it()
    {
        var conversations = new ConversationRepository(_connection);
        await conversations.EnsureOpenAsync("pricing-boundary", T0, cancellationToken: Token);
        await conversations.ForceConcludeAsync("pricing-boundary", null, T0, cancellationToken: Token);

        var assigned = await _exchanges.AssignConversationAsync(Orders, "pricing-boundary", senderIsPerson: true, cancellationToken: Token);

        Assert.Equal(RequestRejection.ConversationClosed, assigned.Refusal!.Rejection);
        Assert.Equal(ConversationCloser.Person, assigned.Refusal.ClosedBy);
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _exchanges.GetOpenAsync(cancellationToken: cts.Token));
    }

    private T Scalar<T>(string sql, long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", id);
        return (T)command.ExecuteScalar()!;
    }

    private void Retire(AgentName agent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE agents SET status = 'retired' WHERE name = @Name;";
        command.Parameters.AddWithValue("@Name", agent.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Takes an agent's machine off the air, the way a lost node does.</summary>
    /// <param name="agent">The agent to strand.</param>
    private void GoSilent(AgentName agent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE agents SET connected = 0 WHERE name = @Name;
            UPDATE nodes  SET connected = 0
             WHERE machine = (SELECT machine FROM agents WHERE name = @Name);
            """;
        command.Parameters.AddWithValue("@Name", agent.Value);
        command.ExecuteNonQuery();
    }
}
