using System.Globalization;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Several sweeps applying the retention policy to one database at once.
/// </summary>
/// <remarks>
/// <para>
/// Not a hypothetical. Every agent's server prunes on its own schedule, an operator may run
/// <c>prune</c> beside them, and SPEC.md §4.1 contemplates several brokers on one project. So
/// losing the race is the ordinary case rather than the exceptional one, and what matters is
/// that it costs nothing and is reported for what it is.
/// </para>
/// <para>
/// A file rather than an in-memory database, because an in-memory one is private to its
/// connection: a test that opened six of those would have six empty databases and would pass
/// against any implementation at all. This is the same trap the in-process concurrency test for
/// the migrator fell into, which passed four hundred threaded attempts against a broken
/// migrator.
/// </para>
/// </remarks>
public sealed class ConcurrentPruneTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-prune-race-" + Guid.NewGuid().ToString("N"));

    private readonly string _path;

    public ConcurrentPruneTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "parley.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort. A pooled connection can still hold the file, and the alternative --
            // SqliteConnection.ClearAllPools() -- is process-global and disposes handles
            // that other test classes are using. See BannedSymbols.txt.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fills the log with settled, delivered traffic that every rule releases.</summary>
    /// <param name="messages">How many messages to write.</param>
    private async Task Seed(int messages)
    {
        using var connection = ParleyDatabase.OpenAndMigrate(_path);

        var agents = new AgentRepository(connection);

        foreach (var name in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Offline), Ct);
        }

        var exchanges = new ExchangeRepository(connection);
        var repository = new MessageRepository(connection);

        // One exchange, closed, so nothing is held by the floor's live-exchange clause. The
        // messages differ by payload so deduplication does not refuse them.
        var exchange = (await exchanges.TryOpenAsync(Orders, Billing, T0, "q1", cancellationToken: Ct))
            .Exchange!;

        for (var i = 0; i < messages; i++)
        {
            var text = i.ToString(CultureInfo.InvariantCulture);

            var appended = await repository.AppendAsync(
                exchange.Id, MessageType.Clarify, Orders, Billing, "some-model-9",
                $$"""{"text":"{{text}}"}""", T0, cancellationToken: Ct);

            Assert.True(appended.Stored);
            Assert.True(await repository.MarkDeliveredAsync(appended.Message.Id, T0, Ct));
        }

        Assert.True(exchange.Deliver(T0).Succeeded);
        Assert.True(exchange.Answer(Billing, T0).Succeeded);
        Assert.True(await exchanges.SaveAsync(exchange, cancellationToken: Ct));

    }

    private async Task<long> MessageCount()
    {
        using var connection = ParleyDatabase.Open(_path);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Six_sweeps_at_once_remove_everything_exactly_once()
    {
        const int Messages = 600;
        await Seed(Messages);

        var sweeps = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var connection = ParleyDatabase.Open(_path);

            return await new Pruner(connection)
                .PruneAsync(RetentionPolicy.Default, T0.AddDays(10), Ct);
        })));

        // Nothing failed, the log is empty, and every message was removed by exactly one sweep.
        Assert.Equal(0, await MessageCount());
        Assert.Equal(Messages, sweeps.Sum(s => s.Removed));
    }

    [Fact]
    public async Task Losing_the_race_is_not_reported_as_the_floor_holding_traffic()
    {
        // The defect this test exists for. The first version of the accounting folded "another
        // sweep removed it" into "the floor kept it", so six concurrent sweeps over thirty
        // thousand spent messages had four of them reporting thirty thousand held. An operator
        // reading that would be told their agents had stopped collecting their inboxes and their
        // exchanges were stuck open, on a deployment where nothing at all was wrong.
        //
        // Enough messages to span several delete batches, so the sweeps genuinely overlap rather
        // than one finishing before the others have opened a connection.
        const int Messages = 2000;
        await Seed(Messages);

        var sweeps = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var connection = ParleyDatabase.Open(_path);

            return await new Pruner(connection)
                .PruneAsync(RetentionPolicy.Default, T0.AddDays(10), Ct);
        })));

        // Nothing here is floor-protected — every message is delivered, on a closed exchange,
        // and long past the deduplication window — so under any interleaving whatsoever no sweep
        // may report one. That is the assertion the defect fails, and it does not depend on which
        // sweep won.
        Assert.All(sweeps, s => Assert.Equal(0, s.HeldByFloor));

        // No sweep claims more than there was.
        Assert.All(sweeps, s => Assert.True(
            s.Removed + s.Contended <= Messages,
            $"a sweep claimed {s.Removed + s.Contended} of {Messages}"));

        // A sweep reporting nothing at all is not a gap in the accounting: its scan ran after
        // another had finished, so it saw no spent message to have an opinion about. That is the
        // quiet pass, and it is tested for its own sake below.
        Assert.Equal(Messages, sweeps.Sum(s => s.Removed));
    }

    [Fact]
    public async Task A_first_drain_is_spread_over_several_passes_and_says_so()
    {
        // The bound on the one pass that is dangerous. Removing two hundred thousand messages in
        // a single pass took four seconds and grew the write-ahead log enough to force a
        // checkpoint, and a concurrent writer measured a wait of 2.3 seconds against a
        // busy_timeout of five — so a log twice that size would surface SQLITE_BUSY to an agent
        // that asked a question. Retention is not a deadline, so a backlog may drain over
        // several sweeps instead.
        //
        // What must not happen is that it drains quietly: an operator told "removed 10,000" and
        // nothing else would believe the log was inside its policy when it was not.
        const int Messages = 10_500;
        await Seed(Messages);

        using var connection = ParleyDatabase.Open(_path);
        var pruner = new Pruner(connection);

        var first = await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(10), Ct);

        Assert.True(first.Capped, "a pass with more than its limit to remove must say so");
        Assert.True(
            first.Removed < Messages,
            $"a single pass removed all {Messages}, so nothing is bounding a first drain");

        // The remainder is not lost, and the pass that finishes it does not claim to be capped.
        var second = await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(10), Ct);

        Assert.False(second.Capped);
        Assert.Equal(Messages, first.Removed + second.Removed);
        Assert.Equal(0, await MessageCount());
    }

    [Fact]
    public async Task A_sweep_that_arrives_after_the_work_is_done_reports_a_quiet_pass()
    {
        // The other side of contention: a sweep whose scan finds nothing at all is not the same
        // as one that found work and lost it, and must not report as though it were.
        await Seed(50);

        using var first = ParleyDatabase.Open(_path);
        var cleared = await new Pruner(first).PruneAsync(RetentionPolicy.Default, T0.AddDays(10), Ct);

        Assert.Equal(50, cleared.Removed);

        using var second = ParleyDatabase.Open(_path);
        var quiet = await new Pruner(second).PruneAsync(RetentionPolicy.Default, T0.AddDays(10), Ct);

        Assert.Equal(0, quiet.Removed);
        Assert.Equal(0, quiet.HeldByFloor);
        Assert.Equal(0, quiet.Contended);
    }
}
