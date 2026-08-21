using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Two sessions for one agent, and a project whose database directory does not exist yet.
/// </summary>
/// <remarks>
/// Both were found by probing rather than by reading, and both are ordinary rather than
/// exotic — a second editor window on a repository somebody is already working in, and the
/// first run of any project at all.
/// </remarks>
public sealed class SecondSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_second_window_on_one_repository_does_not_release_the_first_ones_work()
    {
        // Before the live check this destroyed the exchange outright: the first session's answer
        // was refused as not-open, and the second session's inbox was empty — so the question
        // was gone and neither side had been told anything.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billingA) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            to: "billing",
            intent: "Deciding where the tax rate lives.",
            subQuestions: [new SubQuestion("q1", "Does an invoice carry the rate at time of sale?")],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();

        // The first session picks the question up and starts working on it.
        Assert.Single((await billingA.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        // Somebody opens a second window on the same repository.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.LaunchAsync("billing", ct);

        var answered = await billingA.AnswerAsync(
            exchangeId,
            [new Resolution("q1", "answered", "Yes. invoice_lines.tax_rate is stamped at issue.")],
            cancellationToken: ct);

        Assert.True(
            answered["ok"]?.GetValue<bool>() ?? false,
            $"the first session's answer should still land, got {answered.ToJsonString()}");
    }

    [Fact]
    public async Task Closing_one_of_two_windows_does_not_release_the_others_work()
    {
        // The same defect reached through the other door. A clean shutdown marks the agent
        // offline, and while a boolean carried liveness that was enough for the next attach to
        // conclude nobody was there — even though the first window was mid-answer. It is why
        // liveness is a lease renewed by whichever sessions are running, rather than a flag
        // whichever session leaves last happens to write.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billingA) = await fixture.LaunchAsync("billing", ct);
        var (sessionB, _) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            to: "billing",
            intent: "Deciding where the tax rate lives.",
            subQuestions: [new SubQuestion("q1", "Does an invoice carry the rate at time of sale?")],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();
        Assert.Single((await billingA.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        // The second window closes while the first is still working, and a third opens.
        await fixture.ShutDownAsync(sessionB);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.LaunchAsync("billing", ct);

        var answered = await billingA.AnswerAsync(
            exchangeId,
            [new Resolution("q1", "answered", "Yes. invoice_lines.tax_rate is stamped at issue.")],
            cancellationToken: ct);

        Assert.True(
            answered["ok"]?.GetValue<bool>() ?? false,
            $"the working session's answer should still land, got {answered.ToJsonString()}");
    }

    [Fact]
    public async Task A_long_running_session_keeps_its_lease_by_sweeping()
    {
        // What makes the lease a lease rather than a five-minute grace period. A session that has
        // been open all afternoon has not been heard from since it attached unless something
        // renews the mark, and the sweep is that something — so without the heartbeat this agent
        // would look departed after five minutes and the next window to open would release the
        // work it is doing. The whole session is inside one working window; only the lease has
        // elapsed several times over.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (sessionA, billingA) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            to: "billing",
            intent: "Deciding where the tax rate lives.",
            subQuestions: [new SubQuestion("q1", "Does an invoice carry the rate at time of sale?")],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();
        Assert.Single((await billingA.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        // An hour of ordinary operation: the session is up and its sweep runs every minute.
        for (var minute = 0; minute < 60; minute++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await sessionA.RunAsync((broker, now, c) => broker.TickAsync(now, c), ct);
        }

        // Somebody opens a second window.
        await fixture.LaunchAsync("billing", ct);

        var answered = await billingA.AnswerAsync(
            exchangeId,
            [new Resolution("q1", "answered", "Yes. invoice_lines.tax_rate is stamped at issue.")],
            cancellationToken: ct);

        Assert.True(
            answered["ok"]?.GetValue<bool>() ?? false,
            $"an hour of sweeping should have kept the lease, got {answered.ToJsonString()}");
    }

    [Fact]
    public async Task A_session_that_stopped_long_ago_does_release_its_debts()
    {
        // The other side, so the lease is not simply "never release anything". Once it has
        // lapsed the next attach releases what the previous session left, which is what stops a
        // peer waiting out a four-hour working window for an agent that came back in seconds.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (billingSession, billing) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            to: "billing",
            intent: "Deciding where the tax rate lives.",
            subQuestions: [new SubQuestion("q1", "Does an invoice carry the rate at time of sale?")],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        await fixture.ShutDownAsync(billingSession);
        fixture.Clock.Advance(TimeSpan.FromHours(2));

        var (_, billingAgain) = await fixture.LaunchAsync("billing", ct);

        var refused = await billingAgain.AnswerAsync(
            exchangeId,
            [new Resolution("q1", "answered", "Yes, it does.")],
            cancellationToken: ct);

        Assert.False(refused["ok"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task A_project_whose_parley_directory_does_not_exist_yet_still_starts()
    {
        // The first run of every project. PARLEY_DB conventionally names .parley/parley.db and
        // nothing has created .parley, which SQLite reports as "unable to open database file" —
        // true, and no help at all to somebody setting this up for the first time.
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "parley-fresh-" + Guid.NewGuid().ToString("N"));
        var options = new ServerOptions(
            Path.Combine(root, ".parley", "parley.db"),
            Parley.Core.AgentName.Create("orders"),
            "test-model",
            TimeSpan.FromMinutes(1),
            Parley.Core.RetentionPolicy.Default,
            null);

        try
        {
            var session = await BrokerSession.StartAsync(options, new StubClock(T0), ct);
            await session.DisposeAsync();

            Assert.True(File.Exists(options.DatabasePath));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort, like the fixture's. Microsoft.Data.Sqlite pools connections, so
                // disposing one returns it rather than closing the file — and clearing the pool
                // to force that is process-wide, which would reach into whatever else is running
                // in parallel. A directory left under the temporary path is the cheaper problem.
            }
        }
    }
}
