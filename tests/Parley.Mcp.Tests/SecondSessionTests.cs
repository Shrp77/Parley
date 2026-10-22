using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Two sessions for one agent, and a project whose database directory does not exist yet.
/// </summary>
/// <remarks>
/// Both cases are ordinary rather than
/// exotic — a second editor window on a repository somebody is already working in, and the
/// first run of any project at all.
/// </remarks>
public sealed class SecondSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static async Task<long> RequestAsync(ParleyTools orders, CancellationToken ct)
    {
        var sent = await orders.RequestAsync(
            to: "billing@test",
            ask: "Does an invoice carry the tax rate at time of sale?",
            cancellationToken: ct);

        return sent["exchange_id"]!.GetValue<long>();
    }

    private static Task<JsonNode> ResultAsync(ParleyTools billing, long exchangeId, CancellationToken ct) =>
        billing.ResultAsync(
            exchangeId, "answered", "Yes. invoice_lines.tax_rate is stamped at issue.",
            cancellationToken: ct);

    [Fact]
    public async Task A_second_window_on_one_repository_does_not_take_the_first_ones_work()
    {
        // Before the live check this destroyed the exchange outright: the first session's answer
        // was refused as not-open, and the second session's inbox was empty — so the work was
        // gone and neither side had been told anything.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billingA) = await fixture.LaunchAsync("billing@test", ct);

        var exchangeId = await RequestAsync(orders, ct);

        // The first session picks the Request up and starts working on it.
        Assert.Single((await billingA.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        // Somebody opens a second window on the same repository. It is not handed the work again,
        // because the agent is already attached and working.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var (_, billingB) = await fixture.LaunchAsync("billing@test", ct);

        Assert.Empty((await billingB.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        var resolved = await ResultAsync(billingA, exchangeId, ct);

        Assert.True(
            resolved["ok"]?.GetValue<bool>() ?? false,
            $"the first session's Result should still land, got {resolved.ToJsonString()}");
    }

    [Fact]
    public async Task Closing_one_of_two_windows_does_not_disturb_the_others_work()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billingA) = await fixture.LaunchAsync("billing@test", ct);
        var (sessionB, _) = await fixture.LaunchAsync("billing@test", ct);

        var exchangeId = await RequestAsync(orders, ct);
        Assert.Single((await billingA.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        // The second window closes while the first is still working, and a third opens.
        await fixture.ShutDownAsync(sessionB);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.LaunchAsync("billing@test", ct);

        var resolved = await ResultAsync(billingA, exchangeId, ct);

        Assert.True(
            resolved["ok"]?.GetValue<bool>() ?? false,
            $"the working session's Result should still land, got {resolved.ToJsonString()}");
    }

    [Fact]
    public async Task A_session_that_stopped_is_handed_its_work_again_when_it_returns()
    {
        // An exchange like this is not released when the agent comes back. Nothing times out (§5.3): the work was collected by a session
        // that ended, so the next session is given it again and its Result lands.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (billingSession, billing) = await fixture.LaunchAsync("billing@test", ct);

        var exchangeId = await RequestAsync(orders, ct);
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        await fixture.ShutDownAsync(billingSession);
        fixture.Clock.Advance(TimeSpan.FromHours(2));

        var (_, billingAgain) = await fixture.LaunchAsync("billing@test", ct);

        var redelivered = Assert.Single((await billingAgain.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());
        Assert.Equal(exchangeId, redelivered!["exchange_id"]!.GetValue<long>());

        var resolved = await ResultAsync(billingAgain, exchangeId, ct);

        Assert.True(resolved["ok"]?.GetValue<bool>() ?? false, resolved.ToJsonString());
    }

    [Fact]
    public async Task A_project_whose_parley_directory_does_not_exist_yet_still_starts()
    {
        // The first run of every project. PARLEY_DB conventionally names .parley/parley.db and
        // nothing has created .parley, which SQLite reports as "unable to open database file" —
        // true, and no help at all to somebody setting this up for the first time.
        var root = Path.Combine(Path.GetTempPath(), "parley-fresh-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, ".parley", "parley.db");

        try
        {
            var runtime = Parley.Server.BrokerRuntime.Open(
                Parley.Core.ProjectName.Create("test"), path, Parley.Broker.BrokerOptions.Default, new StubClock(T0));

            await runtime.DisposeAsync();

            Assert.True(File.Exists(path));
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
                // disposing one returns it rather than closing the file — and clearing the pool to
                // force that is process-wide, which would reach into whatever else runs in parallel.
            }
        }
    }
}
