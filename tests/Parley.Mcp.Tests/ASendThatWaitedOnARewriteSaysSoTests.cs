using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>That a send which waited on the broker rewriting its store says so (SPEC.md §4.0, §9.1).</summary>
/// <remarks>
/// Found by review, lens 1 run 115: a rewrite holds every call behind the broker's one gate, seconds on a large store. The
/// owner decided nothing is refused meanwhile and whoever waits is told, so a slow reply reads as a wait rather than as a
/// broker in trouble.
/// </remarks>
[Integration]
public sealed class ASendThatWaitedOnARewriteSaysSoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_queued_behind_a_rewrite_says_it_waited_and_one_that_was_not_says_nothing()
    {
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", Ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", Ct);

        var first = await orders.RequestAsync("billing@test", "Is the total gross or net?", "pricing", cancellationToken: Ct);
        Assert.True((bool)first["ok"]!);
        Assert.Null(first["waited"]);

        // Bulk enough that the rewrite takes a moment, as one that holds anybody up does: the broker says a rewrite has
        // started on the agent's own stream and answers the send on another, so a rewrite of a few milliseconds let the
        // answer arrive first on a slow runner (the nightly of e34fa95), where a real one is seconds long.
        _ = await fixture.Runtime.UseConnectionAsync(
            async (connection, _, ct) =>
            {
                using var bulk = connection.CreateCommand();
                bulk.CommandText =
                    "CREATE TABLE zz_bulk (b BLOB); WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 4000) "
                    + "INSERT INTO zz_bulk SELECT zeroblob(16384) FROM n;";
                return await bulk.ExecuteNonQueryAsync(ct);
            },
            Ct);

        // The broker busy, a destroy queued behind it, and the agent's send behind that: the send runs once the destroy's
        // rewrite has finished.
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var hold = fixture.Runtime.UseConnectionAsync(
            async (_, _, _) =>
            {
                holding.SetResult();
                await release.Task;
                return 0;
            },
            Ct);
        await holding.Task;

        var destroying = fixture.Runtime.UseConnectionAsync(
            (connection, now, ct) => new ConversationRepository(connection).DestroyAsync("pricing", now, cancellationToken: ct),
            Ct);
        await Task.Delay(200, Ct);
        var sending = billing.RequestAsync("orders@test", "Which currency?", cancellationToken: Ct);
        await Task.Delay(200, Ct);

        release.SetResult();
        _ = await hold;
        _ = await destroying;
        var sent = await sending;

        Assert.True((bool)sent["ok"]!, sent.ToJsonString());
        Assert.Contains("waited until it finished", (string?)sent["waited"], StringComparison.Ordinal);
    }
}
