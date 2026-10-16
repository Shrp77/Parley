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
/// SPEC.md §10.2 describes <c>prune</c> as applying the policy "immediately rather than waiting for the sweep",
/// which says plainly that the sweep is where it normally happens. Without that, §9.4 would be a section nothing
/// executed unless somebody remembered to run a command.
/// </para>
/// <para>
/// What the pruner decides is tested in <c>Parley.Store.Tests</c>. What is here is that the tick reaches it, on
/// its own cadence, and that a policy which cannot do its job is refused where the broker is built.
/// </para>
/// </remarks>
public sealed class RetentionSweepTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public RetentionSweepTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> MessageCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Sends, collects and finishes — one exchange that ends properly, and two messages.</summary>
    private static async Task SettledExchange(Broker broker, string conversationId, DateTimeOffset? at = null)
    {
        var start = at ?? T0;

        var sent = await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(conversationId), start, conversationId, Ct);
        Assert.True(sent.Succeeded, $"expected admission, got {sent.Rejection}");

        await broker.DrainInboxAsync(Billing, start.AddMinutes(1), cancellationToken: Ct);

        var finished = await broker.ResultAsync(sent.Exchange!.Id, Billing, Requests.Model, Requests.Result(), start.AddMinutes(2), Ct);
        Assert.True(finished.Succeeded, $"expected the result to store, got {finished.Error}");

        await broker.DrainInboxAsync(Orders, start.AddMinutes(3), cancellationToken: Ct);
    }

    private async Task Close(string conversationId, DateTimeOffset at) =>
        Assert.True(await new ConversationRepository(_connection).ForceConcludeAsync(conversationId, null, at, cancellationToken: Ct));

    [Fact]
    public async Task The_ordinary_tick_applies_the_retention_policy()
    {
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");

        // Inside the idle period the sweep leaves it alone, which matters as much as the other half: a tick that
        // pruned a conversation minutes after it ended would delete the export §10.2 exists to produce.
        var early = await broker.TickAsync(T0.AddDays(2), Ct);

        Assert.Equal(0, early.Retention.Removed);
        Assert.Equal(0, early.Retention.HeldByFloor);
        Assert.Equal(2, await MessageCount());

        Assert.Equal(2, (await broker.TickAsync(T0.AddDays(4), Ct)).Retention.Removed);
        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task A_tick_reports_what_the_floor_is_holding()
    {
        // The number that distinguishes "nothing was old enough" from "plenty was old enough and something is in
        // the way". Work still open holds its text for as long as it is open.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        Assert.True((await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0, "pricing", Ct)).Succeeded);

        var result = await broker.TickAsync(T0.AddDays(400), Ct);

        Assert.Equal(0, result.Retention.Removed);
        Assert.Equal(1, result.Retention.HeldByFloor);
    }

    [Fact]
    public async Task The_notices_of_a_close_wait_to_be_collected()
    {
        // A person closing work writes a closed Result to each party. Retention runs after, and those notices
        // are undelivered, so the floor keeps them: each party is told what happened rather than finding silence.
        var broker = new Broker(_connection, BrokerOptions.Default with { RetentionInterval = TimeSpan.FromTicks(1) });
        await broker.StartAsync(Orders, T0, Ct);

        await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0, "pricing", Ct);
        await broker.DrainInboxAsync(Billing, T0.AddMinutes(1), cancellationToken: Ct);
        await Close("pricing", T0.AddMinutes(2));

        var first = await broker.TickAsync(T0.AddMinutes(10), Ct);

        // The Request stays with them: a notice collected with nothing to say what it closed means nothing (§9.4).
        Assert.Equal(0, first.Retention.Removed);
        Assert.Equal(3, first.Retention.HeldByFloor);

        await broker.DrainInboxAsync(Orders, T0.AddMinutes(11), cancellationToken: Ct);
        await broker.DrainInboxAsync(Billing, T0.AddMinutes(11), cancellationToken: Ct);

        Assert.Equal(3, (await broker.TickAsync(T0.AddMinutes(20), Ct)).Retention.Removed);
    }

    [Fact]
    public async Task Retention_runs_on_its_own_cadence_and_not_on_every_tick()
    {
        // Retention is measured in days, and its pass walks the whole message log — so a sweep every minute spends
        // that to discover something that could not have changed.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");

        Assert.Equal(0, (await broker.TickAsync(T0.AddMinutes(10), Ct)).Retention.Removed);

        // Closing short-circuits the idle period, so everything in the conversation falls due at once.
        await Close("pricing", T0.AddMinutes(11));

        Assert.Equal(0, (await broker.TickAsync(T0.AddMinutes(12), Ct)).Retention.Removed);
        Assert.Equal(2, await MessageCount());

        Assert.Equal(2, (await broker.TickAsync(T0.AddMinutes(70), Ct)).Retention.Removed);
    }

    [Fact]
    public async Task A_broker_that_has_just_started_prunes_on_its_first_tick()
    {
        // Otherwise a project whose agents are started per Request and exit again would never reach the interval.
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
        var broker = new Broker(_connection, BrokerOptions.Default with { RetentionInterval = TimeSpan.FromTicks(1) });
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");

        Assert.Equal(0, (await broker.TickAsync(T0.AddMinutes(10), Ct)).Retention.Removed);

        await Close("pricing", T0.AddMinutes(11));

        Assert.Equal(2, (await broker.TickAsync(T0.AddMinutes(12), Ct)).Retention.Removed);
    }

    [Fact]
    public async Task A_clock_that_moved_backwards_does_not_suspend_retention()
    {
        // The stamp is per process and compared by subtraction, so one tick with a clock an hour fast would
        // otherwise skip every sweep until real time caught up — silently.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        await SettledExchange(broker, "pricing");
        await Close("pricing", T0.AddMinutes(4));

        Assert.Equal(2, (await broker.TickAsync(T0.AddHours(1), Ct)).Retention.Removed);

        await SettledExchange(broker, "second", T0.AddMinutes(5));
        await Close("second", T0.AddMinutes(9));

        Assert.Equal(2, (await broker.TickAsync(T0.AddMinutes(10), Ct)).Retention.Removed);
    }

    // ---- configuration ----

    [Fact]
    public void An_interval_of_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Broker(_connection, BrokerOptions.Default with { RetentionInterval = TimeSpan.Zero }));
    }

    [Fact]
    public void A_short_age_cap_is_accepted_because_nothing_about_open_work_expires()
    {
        // A cap shorter than an exchange could live would let a question expire before its own
        // answer was due. Open work is held by the floor for as long as it is open, so a short cap reaches
        // only what has finished.
        _ = new Broker(_connection, BrokerOptions.Default with { Retention = new RetentionPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(2)) });
    }

    [Fact]
    public void An_incoherent_retention_policy_is_refused_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Broker(
            _connection, BrokerOptions.Default with { Retention = new RetentionPolicy(TimeSpan.FromDays(60), TimeSpan.FromDays(30)) }));
    }

    [Fact]
    public void A_missing_retention_policy_is_refused_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new Broker(_connection, BrokerOptions.Default with { Retention = null! }));
    }
}
