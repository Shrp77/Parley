using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The retention policy of SPEC.md §9.4, and the floor that bounds it.
/// </summary>
/// <remarks>
/// <para>
/// Written against the two failure directions rather than the happy path, because both are silent. Pruning
/// too much removes an inbox nobody knew was there; pruning too little leaves the unbounded log §9.4 exists to
/// prevent, and neither reports anything. The counts are the only evidence either way, so most of these
/// assert on what survived rather than on what went.
/// </para>
/// <para>
/// A settled exchange here carries two messages, its Request and its Result, both collected — the smallest
/// piece of finished work there is.
/// </para>
/// </remarks>
public sealed class PrunerTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
        Work.Register(_connection, Orders, Billing);

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
        _conversations = new ConversationRepository(_connection);
        _pruner = new Pruner(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Sends a Request and, unless told otherwise, has it collected.</summary>
    private async Task<(Exchange Exchange, long RequestId)> Request(
        DateTimeOffset at, string text = "q1", string conversation = "pricing", bool delivered = true)
    {
        var (exchange, request) = await Work.OpenAsync(
            _connection, Orders, Billing, at, Work.Request(text), conversation, cancellationToken: Ct);

        if (delivered)
        {
            Assert.True(await _messages.MarkDeliveredAsync(request.Id, at, Ct));
        }

        return (exchange, request.Id);
    }

    /// <summary>Sends the Result and has it collected, so the floor stops applying.</summary>
    private async Task Settle(Exchange exchange, DateTimeOffset at)
    {
        var result = await Work.CloseAsync(_connection, exchange, at, cancellationToken: Ct);
        Assert.True(await _messages.MarkDeliveredAsync(result.Id, at, Ct));
    }

    private async Task<long> MessageCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<PruneResult> Prune(DateTimeOffset at, RetentionPolicy? policy = null) =>
        _pruner.PruneAsync(policy ?? Policy, at, Ct);

    // ---- the idle rule ----

    [Fact]
    public async Task A_finished_conversation_that_has_gone_quiet_is_pruned()
    {
        var (exchange, _) = await Request(T0);
        await Settle(exchange, T0.AddMinutes(5));

        // Inside the idle period, nothing goes — and nothing is held either. The floor and the rules are
        // different reasons to keep a message, and a run that reported these as "held" would be saying the
        // work was over and something was in the way.
        var early = await Prune(T0.AddDays(2));

        Assert.Equal(0, early.Removed);
        Assert.Equal(0, early.HeldByFloor);
        Assert.Equal(2, await MessageCount());

        // Past it, the conversation is over.
        Assert.Equal(2, (await Prune(T0.AddDays(4))).Removed);
        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task One_exchange_still_open_keeps_the_whole_conversation()
    {
        // The first half of the idle rule: a line of work that is still being done is not idle merely
        // because its earliest exchange went quiet.
        var (settled, _) = await Request(T0);
        await Settle(settled, T0.AddMinutes(5));

        await Request(T0.AddDays(10), "q2");

        var result = await Prune(T0.AddDays(20));

        Assert.Equal(0, result.Removed);
        Assert.Equal(3, await MessageCount());
    }

    [Fact]
    public async Task Conversations_are_judged_apart()
    {
        // A finished, quiet conversation goes while another is still being worked; grouping that lumped them
        // together would let either hold the other.
        var (done, _) = await Request(T0, conversation: "pricing");
        await Settle(done, T0.AddMinutes(5));

        await Request(T0, "q2", conversation: "stock-levels");

        var result = await Prune(T0.AddDays(4));

        Assert.Equal(2, result.Removed);
        Assert.Equal(1, await MessageCount());
    }

    [Fact]
    public async Task Later_work_in_the_same_conversation_keeps_the_earlier_messages()
    {
        // The second half of the idle rule, and the half a per-message age check would miss. An old message in
        // a conversation somebody returned to last night is not idle, and pruning it would leave the export
        // showing a Result for a Request that is no longer there.
        var (first, _) = await Request(T0);
        await Settle(first, T0.AddMinutes(5));

        var (second, _) = await Request(T0.AddDays(10), "q2");
        await Settle(second, T0.AddDays(10).AddMinutes(5));

        Assert.Equal(0, (await Prune(T0.AddDays(12))).Removed);
        Assert.Equal(4, await MessageCount());

        // Once the conversation itself has been quiet for the idle period, all of it goes together.
        Assert.Equal(4, (await Prune(T0.AddDays(14))).Removed);
    }

    [Fact]
    public async Task Closing_a_conversation_waits_out_the_idle_period()
    {
        var (exchange, _) = await Request(T0);
        await Settle(exchange, T0.AddMinutes(5));

        // Agents close a conversation by consensus with nobody present, so a closed one is kept as long as a quiet one:
        // the person can still read it back and export it.
        Assert.True(await _conversations.ForceConcludeAsync("pricing", null, T0.AddMinutes(10), cancellationToken: Ct));

        Assert.Equal(0, (await Prune(T0.AddMinutes(20))).Removed);
        Assert.Equal(0, (await Prune(T0.AddDays(3))).Removed);
        Assert.Equal(2, (await Prune(T0.AddDays(3).AddMinutes(6))).Removed);
    }

    // ---- the floor ----

    [Fact]
    public async Task An_exchange_that_is_still_open_keeps_its_request_past_the_age_cap()
    {
        // The clause that must stay absolute. Nothing ends a piece of work but its participants or its person,
        // so nothing deletes its text on a schedule either: the flag puts it in front of the person, who closes
        // it.
        await Request(T0);

        var aggressive = new RetentionPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
        var result = await Prune(T0.AddDays(365), aggressive);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.HeldByFloor);
        Assert.Equal(1, await MessageCount());
    }

    [Fact]
    public async Task Once_the_person_closes_it_the_ordinary_rules_apply()
    {
        // What bounds the absolute floor above. The broker's closed Results are messages like any other, and
        // are pruned with the rest of the conversation.
        var (exchange, _) = await Request(T0);

        Assert.True(await _conversations.ForceConcludeAsync("pricing", "nobody is working on this", T0.AddDays(60), cancellationToken: Ct));
        await DeliverEverything();

        // Once the closed Results have been quiet for the idle period, which closing does not shorten.
        var result = await Prune(T0.AddDays(64));

        Assert.Equal(0, result.HeldByFloor);
        Assert.Equal(0, await MessageCount());
        Assert.Equal(ExchangeOutcome.Closed, (await _exchanges.GetAsync(exchange.Id, cancellationToken: Ct))!.Outcome);
    }

    [Fact]
    public async Task An_undelivered_message_survives_the_idle_rule()
    {
        var (exchange, _) = await Request(T0, delivered: false);
        await Settle(exchange, T0.AddMinutes(5));

        var result = await Prune(T0.AddDays(4));

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.HeldByFloor);
    }

    [Fact]
    public async Task An_undelivered_message_does_not_survive_the_age_cap()
    {
        // The clause with no terminal state. An agent that never attaches never collects, and nothing else
        // clears an inbox nobody reads — so if the cap did not reach this, the one unbounded corner of the log
        // would be the part carrying full text.
        var (exchange, _) = await Request(T0, delivered: false);
        await Settle(exchange, T0.AddMinutes(5));

        var result = await Prune(T0.AddDays(31));

        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.HeldByFloor);
        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task A_message_inside_the_deduplication_window_is_kept()
    {
        // The row is what the exact check of §13.1 reads, so removing it would let a byte-identical message
        // through. An operator tightening retention must not be able to break deduplication as a side effect.
        var (exchange, _) = await Request(T0);
        await Settle(exchange, T0);

        // Written after the close, at the exchange's version as it now stands, so the same append below is
        // the same identity. The settled Result was written before closing moved the version, and repeating it
        // now is a different message (§13.1).
        var written = await _messages.AppendAsync(
            exchange.Id, MessageType.Result, Billing, Orders, Work.Model, Work.Result(), T0, cancellationToken: Ct);

        Assert.True(written.Stored);
        Assert.True(await _messages.MarkDeliveredAsync(written.Message.Id, T0, Ct));

        var aggressive = new RetentionPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        var held = await Prune(T0.AddSeconds(10), aggressive);

        Assert.Equal(0, held.Removed);
        Assert.Equal(3, held.HeldByFloor);

        // And the mechanism it protects still works, which is the property rather than the row.
        var duplicate = await _messages.AppendAsync(
            exchange.Id, MessageType.Result, Billing, Orders, Work.Model, Work.Result(), T0.AddSeconds(10),
            cancellationToken: Ct);

        Assert.False(duplicate.Stored);
    }

    [Fact]
    public async Task A_request_is_kept_while_its_result_waits_to_be_collected()
    {
        // The Result is held by the undelivered floor, so the Request it answers, delivered days before, must be
        // held too: four days on, a requester that had not run since collected a Result
        // with nothing carried back to say what it answered (§4.2). Nothing times out, so a requester
        // away for days is ordinary rather than an edge.
        var (exchange, requestId) = await Request(T0);
        var result = await Work.CloseAsync(_connection, exchange, T0.AddMinutes(1), cancellationToken: Ct);

        var early = await Prune(T0.AddDays(4), RetentionPolicy.Default);

        Assert.NotNull(await _messages.GetAsync(requestId, cancellationToken: Ct));
        Assert.NotNull(await _messages.GetAsync(result.Id, cancellationToken: Ct));
        Assert.Equal(0, early.Removed);

        // And it is not a way round the age cap: once that reaches the Result, the Request goes with it.
        await Prune(T0.AddDays(31), RetentionPolicy.Default);

        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task Past_the_window_the_same_messages_go()
    {
        // The other side of the previous test. Without this, "kept inside the window" would pass just as well
        // against a pruner that kept everything for ever.
        var (exchange, _) = await Request(T0);
        await Settle(exchange, T0);

        var aggressive = new RetentionPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var result = await Prune(T0.AddMinutes(5), aggressive);

        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.HeldByFloor);
    }

    // ---- what is not the target ----

    [Fact]
    public async Task The_exchange_and_what_its_result_said_outlive_the_text()
    {
        // §9.4: exchange rows and result items carry no free prose and are what §15 is computed from, so
        // pruning the text while keeping the skeleton is what lets a deployment answer "how often is this
        // receiver blocked" a year later.
        var (exchange, _) = await Request(T0);
        await Settle(exchange, T0.AddMinutes(5));

        Assert.Equal(2, (await Prune(T0.AddDays(4))).Removed);

        var survivor = await _exchanges.GetAsync(exchange.Id, cancellationToken: Ct);

        Assert.Equal(ExchangeState.Closed, survivor!.State);
        Assert.Equal(ExchangeOutcome.Complete, survivor.Outcome);
        Assert.NotNull(await _conversations.GetAsync("pricing", cancellationToken: Ct));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM result_items;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(Ct))!);
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
        // What the pruner rests on. It scans for identifiers outside a transaction and deletes by them
        // afterwards, so an identifier reissued in between would let a decision made about one message remove a
        // different one. SQLite never reissues under AUTOINCREMENT, so this is the schema keyword the whole
        // arrangement depends on.
        var (exchange, first) = await Request(T0);
        await Settle(exchange, T0.AddMinutes(5));

        Assert.Equal(2, (await Prune(T0.AddDays(4))).Removed);

        var (_, reused) = await Request(T0.AddDays(5), "q2", conversation: "stock-levels");

        Assert.True(reused > first, $"message {first} was reissued as {reused}");
    }

    [Fact]
    public async Task An_unusable_policy_is_refused_rather_than_applied()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _pruner.PruneAsync(new RetentionPolicy(TimeSpan.FromDays(30), TimeSpan.FromDays(3)), T0, Ct));

        await Assert.ThrowsAsync<ArgumentNullException>(() => _pruner.PruneAsync(null!, T0, Ct));
    }

    private async Task DeliverEverything()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE messages SET delivered_utc = created_utc WHERE delivered_utc IS NULL;";
        await command.ExecuteNonQueryAsync(Ct);
    }
}
