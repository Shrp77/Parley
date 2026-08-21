using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

public sealed class ExchangeRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delivery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Working = TimeSpan.FromHours(4);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(3);

    private static readonly ExchangePolicy Policy = ExchangePolicy.Default with
    {
        DeliveryWindow = Delivery,
        WorkingWindow = Working,
        GracePeriod = Grace
    };

    // Distinctness is keyed on question content, so a test opening two exchanges on one pair
    // has to say they are different questions. Naming them here keeps that visible at each
    // call rather than hidden in a helper: "q1" and "q2" reading differently is the point.
    private static string Q(string text) => text;

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;

    public ExchangeRepositoryTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null, AgentStatus.Registered),
                TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Opening_persists_and_round_trips()
    {
        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded);

        var loaded = await _exchanges.GetAsync(
            outcome.Exchange!.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(Orders, loaded.Asker);
        Assert.Equal(Billing, loaded.Answerer);
        Assert.Equal(Billing, loaded.OwedBy);
        Assert.Equal(ExchangeState.Open, loaded.State);
        Assert.Equal(T0, loaded.OpenedUtc);
        // The delivery window runs first: nothing has collected the question yet.
        Assert.Equal(T0 + Delivery, loaded.ExpiresUtc);
        Assert.Equal(Delivery, loaded.DeliveryWindow);
        Assert.Equal(Working, loaded.WorkingWindow);
        Assert.Equal(Grace, loaded.GracePeriod);
        Assert.Null(loaded.DeliveredUtc);
        Assert.Null(loaded.Resolution);
        Assert.Equal(0L, loaded.Version);
    }

    [Fact]
    public async Task Sub_second_windows_survive_the_round_trip()
    {
        // Milliseconds rather than seconds in storage. Seconds truncated the window while
        // expires_utc kept full precision, leaving a rehydrated exchange unable to reproduce
        // its own deadline.
        var odd = TimeSpan.FromMilliseconds(1500);
        var oddWorking = TimeSpan.FromMilliseconds(2500);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"),
            Policy with { DeliveryWindow = odd, WorkingWindow = oddWorking },
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await _exchanges.GetAsync(
            outcome.Exchange!.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(odd, loaded.DeliveryWindow);
        Assert.Equal(oddWorking, loaded.WorkingWindow);
        // The stored deadline and the stored window must agree with each other.
        Assert.Equal(loaded.OpenedUtc + loaded.DeliveryWindow, loaded.ExpiresUtc);
    }

    [Fact]
    public async Task A_policy_outside_what_storage_accepts_is_rejected()
    {
        // Checked here as well as in the schema, so a misconfigured deployment fails naming the
        // field rather than on the first question with a constraint violation raised several
        // layers below the mistake.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, Q("q1"),
                Policy with { DeliveryWindow = TimeSpan.Zero },
                cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, Q("q1"),
                Policy with { GracePeriod = TimeSpan.FromMilliseconds(1) },
                cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, Q("q1"),
                Policy with { OpenPairCap = 0 },
                cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, Q("q1"),
                Policy with { OpenPairCap = ExchangePolicy.MaximumOpenPairCap + 1 },
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_question_must_carry_its_content_identity()
    {
        // Distinctness is keyed on it, so an empty hash would make every question identical.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, "  ",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Negative_clarify_depth_cap_is_rejected_as_an_argument()
    {
        // Without the up-front check this reached the schema and surfaced as a
        // SqliteException, leaking the storage layer through an API whose documented
        // failures are argument exceptions. The caller cannot act differently on the two,
        // and only one of them names the parameter at fault.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _exchanges.TryOpenAsync(
                Orders, Billing, T0, Q("q1"), Policy with { ClarifyDepthCap = -1 },
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_reverse_question_is_refused_as_a_cycle_rather_than_as_a_busy_pair()
    {
        // Under pair serialisation this was the two-party case: billing asking orders back was
        // refused because the pair already held an exchange. Nothing serialises now, so it is
        // refused for the reason it was always really wrong — the two questions depend on each
        // other and neither can be answered. The 2-cycle is no longer a special case; the same
        // walk that catches three agents catches two.
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"),
            cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Billing, Orders, T0, Q("q2"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.WouldDeadlock, outcome.Rejection);
        Assert.Equal([Billing, Orders], outcome.CyclePath);
    }

    [Fact]
    public async Task A_second_different_question_to_the_same_peer_is_admitted()
    {
        // The behaviour the pair rule used to forbid, and the reason for removing it. Answering
        // across a repository boundary can take hours, and an asker unable to raise anything
        // else with that peer meanwhile has only Notify left.
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("where does price live"),
            cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("who owns the discount ladder"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, $"expected admission, got {outcome.Rejection}");
    }

    [Fact]
    public async Task Repeating_a_live_question_is_refused_and_returns_the_exchange_carrying_it()
    {
        // Distinctness. Repetition used to be impossible because a pair held one exchange, so
        // removing that rule removed a guard nobody had written down as one.
        var first = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("where does price live"),
            cancellationToken: TestContext.Current.CancellationToken);

        var repeat = await _exchanges.TryOpenAsync(
            Orders, Billing, T0.AddMinutes(1), Q("where does price live"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.DuplicateQuestion, repeat.Rejection);
        Assert.Equal(first.Exchange!.Id, Assert.Single(repeat.Outstanding).Id);
    }

    [Fact]
    public async Task The_open_pair_cap_bounds_how_much_one_asker_may_have_in_flight()
    {
        for (var i = 0; i < 3; i++)
        {
            var admitted = await _exchanges.TryOpenAsync(
                Orders, Billing, T0, Q($"question {i}"),
                policy: Policy with { OpenPairCap = 3 }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(admitted.Succeeded, $"question {i} should have been admitted");
        }

        var refused = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("one too many"),
            policy: Policy with { OpenPairCap = 3 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.PairSaturated, refused.Rejection);

        // The refusal hands back work rather than a retry instruction: an asker shown what it
        // is already waiting on can often see that this question is covered by one of them.
        Assert.Equal(3, refused.Outstanding.Count);
    }

    [Fact]
    public async Task The_cap_is_directional()
    {
        // orders filling its allowance with billing must not stop billing asking orders. The
        // two flows are unrelated lines of enquiry, and coupling them would refuse one agent
        // for the sins of another.
        var saturate = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("only question"),
            policy: Policy with { OpenPairCap = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(saturate.Succeeded);

        // Answered, so it no longer holds a wait-for edge and the reverse question is not a
        // cycle. The cap is what is under test here, not cycle detection.
        Assert.True(saturate.Exchange!.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            saturate.Exchange, cancellationToken: TestContext.Current.CancellationToken));

        var reverse = await _exchanges.TryOpenAsync(
            Billing, Orders, T0.AddMinutes(2), Q("a different question"),
            policy: Policy with { OpenPairCap = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(reverse.Succeeded, $"expected admission, got {reverse.Rejection}");
    }

    [Fact]
    public async Task A_question_to_an_unreachable_agent_is_refused_immediately()
    {
        // The failure this exists for: a repository is deleted while its agent is still
        // registered. The agent row survives, so the foreign key is satisfied and the pair slot
        // is free — every other admission check passes. Without this the question is accepted,
        // waits a full inactivity window, orphans unanswered, and costs the same again on the
        // next attempt, forever, while nobody learns why.
        var agents = new AgentRepository(_connection);
        Assert.Equal(1, await agents.MarkUnreachableAsync(
            Billing, TestContext.Current.CancellationToken));

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.AnswererUnreachable, outcome.Rejection);
        Assert.False(outcome.Succeeded);

        // The one refusal that hands back no work, because there is none to hand back.
        Assert.Null(outcome.Exchange);
        Assert.Empty(outcome.Outstanding);
        Assert.Empty(outcome.CyclePath);

        // And nothing was written, so no slot is consumed by a question that cannot land.
        Assert.Empty(await _exchanges.GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_offline_agent_is_still_routable()
    {
        // Offline is the ordinary resting state of a lazily spawned agent, not a fault.
        // Refusing here would refuse the very traffic meant to wake it.
        var agents = new AgentRepository(_connection);
        Assert.Equal(1, await agents.MarkOfflineAsync(Billing, TestContext.Current.CancellationToken));

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task An_agent_that_comes_back_is_routable_again()
    {
        var agents = new AgentRepository(_connection);
        await agents.MarkUnreachableAsync(Billing, TestContext.Current.CancellationToken);
        await agents.MarkLiveAsync(Billing, null, T0, TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task An_unreachable_asker_may_still_ask()
    {
        // The state describes whether the broker can reach an agent, not whether it may speak.
        // An agent running well enough to ask is evidently reachable, and refusing it here
        // would strand it on a stale mark until something else cleared it.
        var agents = new AgentRepository(_connection);
        await agents.MarkUnreachableAsync(Orders, TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task Three_cycle_is_rejected_with_a_path()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        await _exchanges.TryOpenAsync(
            Billing, Inventory, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _exchanges.TryOpenAsync(
            Inventory, Orders, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OpenRejection.WouldDeadlock, outcome.Rejection);
        Assert.Equal([Inventory, Orders, Billing], outcome.CyclePath);
    }

    [Fact]
    public async Task Closing_frees_the_pair_and_the_cycle()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        await _exchanges.TryOpenAsync(
            Billing, Inventory, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        var open = await _exchanges.GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var first = open.Single(e => e.Asker == Orders);

        Assert.True(first.Answer(Billing, T0.AddMinutes(1)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            first, cancellationToken: TestContext.Current.CancellationToken));

        var outcome = await _exchanges.TryOpenAsync(
            Inventory, Orders, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task Clarify_is_persisted_and_reverses_admission()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        var exchange = opened.Exchange!;

        Assert.True(exchange.Clarify(Billing, T0.AddMinutes(2)).Succeeded);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var loaded = await _exchanges.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(Orders, loaded.OwedBy);
        Assert.Equal(1, loaded.ClarifyDepth);
        // A clarification proves the answerer holds the question, so the working window runs.
        Assert.Equal(T0.AddMinutes(2) + Working, loaded.ExpiresUtc);
    }

    [Fact]
    public async Task Save_advances_the_version_stamp()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        var exchange = opened.Exchange!;
        Assert.Equal(0L, exchange.Version);

        exchange.Clarify(Billing, T0);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1L, exchange.Version);

        // The instance must remain usable for a second save without reloading.
        exchange.Clarify(Orders, T0);
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2L, exchange.Version);
    }

    [Fact]
    public async Task Stale_save_is_rejected_and_loses_no_clarify_depth()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        var id = opened.Exchange!.Id;

        // Two independent instances of the same row, as two broker code paths would hold.
        var a = await _exchanges.GetAsync(id, cancellationToken: TestContext.Current.CancellationToken);
        var b = await _exchanges.GetAsync(id, cancellationToken: TestContext.Current.CancellationToken);

        a!.Clarify(Billing, T0);
        Assert.True(await _exchanges.SaveAsync(
            a, cancellationToken: TestContext.Current.CancellationToken));

        // b was loaded before a's write, so its transition must not be applied.
        b!.Clarify(Billing, T0);
        Assert.False(await _exchanges.SaveAsync(
            b, cancellationToken: TestContext.Current.CancellationToken));

        var reloaded = await _exchanges.GetAsync(id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, reloaded!.ClarifyDepth);
        Assert.Equal(1L, reloaded.Version);
    }

    [Fact]
    public async Task Save_of_a_missing_row_reports_failure()
    {
        var orphanInstance = Exchange.Open(999_999, Orders, Billing, T0, Policy);

        Assert.False(await _exchanges.SaveAsync(
            orphanInstance, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Orphaned_exchanges_are_findable_by_the_owing_agent()
    {
        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        var exchange = opened.Exchange!;

        exchange.Orphan(T0.AddMinutes(20));
        Assert.True(await _exchanges.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(await _exchanges.GetOrphanedOwedByAsync(
            Billing, TestContext.Current.CancellationToken));
        Assert.Empty(await _exchanges.GetOrphanedOwedByAsync(
            Orders, TestContext.Current.CancellationToken));

        // An orphan holds no pair slot, so a new question on that pair is admitted.
        var reopened = await _exchanges.TryOpenAsync(
            Billing, Orders, T0.AddHours(1), Q("q1"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(reopened.Succeeded);
    }

    [Fact]
    public async Task Resuming_a_second_orphan_for_the_same_pair_is_refused()
    {
        // ux_open_pair is a partial index over open rows only, so a pair can accumulate more
        // than one orphan: orphan the first, open a second on the freed slot, orphan that
        // too. Both are then returned by GetOrphanedOwedByAsync, and replaying the inbox on
        // re-registration would try to resume both.
        var first = (await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"),
            cancellationToken: TestContext.Current.CancellationToken)).Exchange!;
        first.Orphan(T0.AddMinutes(20));
        Assert.True(await _exchanges.SaveAsync(
            first, cancellationToken: TestContext.Current.CancellationToken));

        var second = (await _exchanges.TryOpenAsync(
            Orders, Billing, T0.AddHours(1), Q("q1"),
            cancellationToken: TestContext.Current.CancellationToken)).Exchange!;
        second.Orphan(T0.AddHours(2));
        Assert.True(await _exchanges.SaveAsync(
            second, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, (await _exchanges.GetOrphanedOwedByAsync(
            Billing, TestContext.Current.CancellationToken)).Count);

        // The first resume takes the pair slot back.
        first.Resume(T0.AddHours(3));
        Assert.True(await _exchanges.SaveAsync(
            first, cancellationToken: TestContext.Current.CancellationToken));

        // The second cannot, and the schema is what stops it. This surfaces as an exception
        // rather than a false return: false means "you are stale, reload and decide again",
        // which would send a caller that retried into a loop, since reloading changes
        // nothing about the pair already being held.
        second.Resume(T0.AddHours(3));
        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => _exchanges.SaveAsync(
                second, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expiry_scan_finds_only_overdue_open_exchanges()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Q("q1"), cancellationToken: TestContext.Current.CancellationToken);
        await _exchanges.TryOpenAsync(
            Billing, Inventory, T0.AddHours(1), Q("q1"),
            cancellationToken: TestContext.Current.CancellationToken);

        var expired = await _exchanges.GetExpiredAsync(
            T0.AddMinutes(30), TestContext.Current.CancellationToken);

        Assert.Single(expired);
        Assert.Equal(Orders, expired[0].Asker);
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _exchanges.GetOpenAsync(cancellationToken: cts.Token));
    }
}
