using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Retention on the ordinary tick, and the configuration that decides it.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §10.2 describes <c>prune</c> as applying the policy "immediately rather than waiting
/// for the sweep", which says plainly that the sweep is where it normally happens. Without that,
/// §9.4 would be a section nothing executed unless somebody remembered to run a command — and an
/// unbounded log is the thing the section exists to prevent, so leaving it to a habit is leaving
/// it undone.
/// </para>
/// <para>
/// The whole of what the pruner decides is tested in <c>Parley.Store.Tests</c>. What is here is
/// that the tick reaches it, that the order of the tick's three passes is the one that lets a
/// sweep finish its own work, and that a policy which cannot do its job is refused where the
/// broker is built rather than discovered from a log that came back shorter than expected.
/// </para>
/// </remarks>
public sealed class RetentionSweepTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Question =
        """{"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"If it does I read it from you; if not I snapshot my own copy and we can diverge."}""";

    private const string Model = "some-model-9";

    private readonly SqliteConnection _connection;

    public RetentionSweepTests()
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
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> MessageCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Asks, collects, and answers — one exchange that ends properly.</summary>
    /// <param name="broker">The broker to run it through.</param>
    /// <param name="conversationId">The discussion to run it in.</param>
    /// <param name="at">When it starts, defaulting to the fixture instant.</param>
    private async Task SettledExchange(
        Broker broker, string conversationId, DateTimeOffset? at = null)
    {
        var start = at ?? T0;

        var asked = await broker.AskAsync(
            Orders, Billing, Model, Question, start, conversationId, Ct);

        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");

        // Collected, so the question is delivered and the floor's inbox clause stops applying.
        await broker.DrainInboxAsync(Billing, start.AddMinutes(1), cancellationToken: Ct);

        var answered = await broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            start.AddMinutes(2), cancellationToken: Ct);

        Assert.True(answered.Succeeded, $"expected the answer to store, got {answered.Error}");

        await broker.DrainInboxAsync(Orders, start.AddMinutes(3), cancellationToken: Ct);
    }

    /// <summary>Closes a discussion by operator decision, so its messages fall due at once.</summary>
    private async Task Conclude(string conversationId, DateTimeOffset at) =>
        Assert.True(await new ConversationRepository(_connection)
            .ForceConcludeAsync(conversationId, at, cancellationToken: Ct));

    [Fact]
    public async Task The_ordinary_tick_applies_the_retention_policy()
    {
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");

        Assert.Equal(2, await MessageCount());

        // Inside the idle period the sweep leaves it alone, which matters as much as the other
        // half: a tick that pruned a discussion three minutes after it ended would delete the
        // export §10.2 exists to produce.
        var early = await broker.TickAsync(T0.AddDays(2), Ct);

        Assert.Equal(0, early.Retention.Removed);
        Assert.Equal(2, await MessageCount());

        // Held is zero here, which is the other reading of a prune that removed nothing: the
        // discussion is simply not finished with yet, and nothing is in the way.
        Assert.Equal(0, early.Retention.HeldByFloor);

        var later = await broker.TickAsync(T0.AddDays(4), Ct);

        Assert.Equal(2, later.Retention.Removed);
        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task A_ghost_written_by_this_tick_is_not_pruned_by_it()
    {
        // The ordering the tick depends on. Ghosting writes a notice to the asker and closes the
        // exchange; retention runs after, so the notice exists before anything judges it. It is
        // undelivered at that moment — nobody has collected it — so the floor keeps it, and the
        // asker is told what §14 promises rather than finding silence where the ghost was.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var asked = await broker.AskAsync(Orders, Billing, Model, Question, T0, null, Ct);
        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");

        // Far past every deadline and far past the age cap, so nothing is protected by youth.
        var swept = await broker.TickAsync(T0.AddDays(400), Ct);
        Assert.Equal(1, swept.Exchanges.Orphaned);

        var ghosting = await broker.TickAsync(T0.AddDays(401), Ct);
        Assert.Equal(1, ghosting.Exchanges.Ghosted);

        // The ghost survived the same tick that wrote it, and reaches the agent it was for.
        var collected = await broker.DrainInboxAsync(Orders, T0.AddDays(401), cancellationToken: Ct);

        Assert.Contains(collected.Messages, m => m.Message.Type == MessageType.Ghost);
    }

    [Fact]
    public async Task A_tick_reports_what_the_floor_is_holding()
    {
        // The number that distinguishes "nothing was old enough" from "plenty was old enough and
        // something is in the way". Those are the two readings of a prune that removed nothing,
        // and they call for opposite responses.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        Assert.True((await broker.AskAsync(Orders, Billing, Model, Question, T0, null, Ct))
            .Succeeded);

        // Well past the age cap, so the question is spent by every rule there is. This same tick
        // orphans the exchange, and an orphan's messages are held absolutely — §14 may replay
        // them. So: nothing removed, and the count says why.
        var result = await broker.TickAsync(T0.AddDays(400), Ct);

        Assert.Equal(1, result.Exchanges.Orphaned);
        Assert.Equal(0, result.Retention.Removed);
        Assert.Equal(1, result.Retention.HeldByFloor);
    }

    [Fact]
    public async Task Retention_runs_on_its_own_cadence_and_not_on_every_tick()
    {
        // The reaper enforces deadlines and must run at the cadence of the shortest one.
        // Retention is measured in days, and its pass is a full walk of the message log under
        // the lock admission takes — so a sweep every minute spends that on every question in
        // the deployment to discover something that could not have changed.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");

        // The first tick after a start prunes whatever the interval says. Nothing is due yet, so
        // it only claims the slot.
        Assert.Equal(0, (await broker.TickAsync(T0.AddMinutes(10), Ct)).Retention.Removed);

        // Concluding short-circuits the idle period, so everything in the discussion falls due
        // at once — which is what makes this observable inside an hour at all.
        await Conclude("pricing", T0.AddMinutes(11));

        // Due, and not looked at: the interval has not elapsed.
        Assert.Equal(0, (await broker.TickAsync(T0.AddMinutes(12), Ct)).Retention.Removed);
        Assert.Equal(2, await MessageCount());

        // An hour after the last pass, it runs.
        Assert.Equal(2, (await broker.TickAsync(T0.AddMinutes(70), Ct)).Retention.Removed);
    }

    [Fact]
    public async Task A_broker_that_has_just_started_prunes_on_its_first_tick()
    {
        // Otherwise a project whose agents are spawned per question and exit again would never
        // reach the interval, and its log would grow without bound while every server reported
        // a healthy sweep.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");

        var restarted = new Broker(_connection, BrokerOptions.Default);
        await restarted.StartAsync(Orders, T0.AddDays(4), Ct);

        Assert.Equal(2, (await restarted.TickAsync(T0.AddDays(4), Ct)).Retention.Removed);
    }

    [Fact]
    public async Task An_interval_can_be_shortened_to_the_tick()
    {
        // The dial exists, and the every-tick behaviour is reachable through it. A deployment
        // that wants retention applied the moment something falls due can have it; it is a
        // decision with a cost rather than the default.
        var options = BrokerOptions.Default with { RetentionInterval = TimeSpan.FromTicks(1) };

        var broker = new Broker(_connection, options);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");

        Assert.Equal(0, (await broker.TickAsync(T0.AddMinutes(10), Ct)).Retention.Removed);

        await Conclude("pricing", T0.AddMinutes(11));

        // The same two minutes that changed nothing under the default.
        Assert.Equal(2, (await broker.TickAsync(T0.AddMinutes(12), Ct)).Retention.Removed);
    }

    [Fact]
    public async Task A_clock_that_moved_backwards_does_not_suspend_retention()
    {
        // The stamp is per process and compared by subtraction, so one tick with a clock an hour
        // fast would otherwise skip every sweep until real time caught up. Retention stopping is
        // silent: a log that is not shrinking looks exactly like a log with nothing due.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");
        await Conclude("pricing", T0.AddMinutes(4));

        // A tick stamped an hour into the future, which prunes and claims the slot.
        Assert.Equal(2, (await broker.TickAsync(T0.AddHours(1), Ct)).Retention.Removed);

        await SettledExchange(broker, "second", T0.AddMinutes(5));
        await Conclude("second", T0.AddMinutes(9));

        // Time corrected. Under a plain subtraction this and every tick for the next hour would
        // do nothing at all.
        Assert.Equal(2, (await broker.TickAsync(T0.AddMinutes(10), Ct)).Retention.Removed);
    }

    // ---- configuration ----

    [Fact]
    public void An_interval_of_zero_is_refused()
    {
        // It would restore the every-tick sweep the interval exists to replace, and do it
        // silently: the deployment would work, and pay a full pass over the log for every
        // question anybody asked.
        var options = BrokerOptions.Default with { RetentionInterval = TimeSpan.Zero };

        Assert.Throws<ArgumentOutOfRangeException>(() => new Broker(_connection, options));
    }


    [Fact]
    public void An_age_cap_shorter_than_an_exchange_lives_is_refused()
    {
        // Nothing is corrupted by it — the floor holds a live exchange's messages absolutely, so
        // the cap simply never reaches them — but it is a policy whose stated effect cannot
        // occur, and the two numbers live in different objects, so nothing else is positioned to
        // notice. The default exchange policy allows fifteen minutes to collect, four hours to
        // answer and three to return; two hours of retention would expire a question before its
        // own answer was due.
        var options = BrokerOptions.Default with
        {
            Retention = new RetentionPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
        };

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => new Broker(_connection, options));

        Assert.Contains("shorter than the longest an exchange may live", refusal.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_shorter_exchange_policy_makes_the_same_cap_acceptable()
    {
        // The other side, and the reason the check is against the exchange policy rather than a
        // constant: the two are related, not independently sensible. A deployment whose
        // exchanges are minutes long may legitimately keep hours of log.
        var options = BrokerOptions.Default with
        {
            ExchangePolicy = new ExchangePolicy(
                DeliveryWindow: TimeSpan.FromMinutes(5),
                WorkingWindow: TimeSpan.FromMinutes(20),
                GracePeriod: TimeSpan.FromMinutes(10),
                ClarifyDepthCap: 2,
                OpenPairCap: 1),
            Retention = new RetentionPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
        };

        _ = new Broker(_connection, options);
    }

    [Fact]
    public void An_incoherent_retention_policy_is_refused_at_construction()
    {
        var options = BrokerOptions.Default with
        {
            Retention = new RetentionPolicy(TimeSpan.FromDays(60), TimeSpan.FromDays(30))
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new Broker(_connection, options));
    }

    [Fact]
    public void A_missing_retention_policy_is_refused_at_construction()
    {
        var options = BrokerOptions.Default with { Retention = null! };

        Assert.Throws<ArgumentNullException>(() => new Broker(_connection, options));
    }
}
