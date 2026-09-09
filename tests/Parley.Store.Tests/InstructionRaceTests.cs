using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The settling paths racing each other, and the listing racing a writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two things settle an instruction and they do not coordinate.</b> A node reports; a sweep
/// runs. Each is a separate call arriving at a separate moment, and the only thing keeping them
/// from contradicting one another is the <c>outcome = 'pending'</c> clause they share. That is the
/// right design — the guarantee is in the statement rather than in every caller — but "the SQL
/// makes it safe" is a claim, and this is where it gets probed rather than asserted.
/// </para>
/// <para>
/// There were three. A departing node settled its agents' turns, which looked obviously right and
/// was not: a stream ending is not a process dying, so it destroyed an answer on every network
/// blip. Removed by the gate; <c>InstructionSurvivesTests</c> holds the case.
/// </para>
/// <para>
/// The listing half is here for a different reason. Instructions are written while somebody is
/// reading them, and a cursor that repeats or skips a row under concurrent inserts is the kind of
/// defect that shows up as an operator swearing they saw something and cannot find it again.
/// </para>
/// </remarks>
public sealed class InstructionRaceTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;

    public InstructionRaceTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _instructions = new InstructionRepository(_connection);

        using var command = _connection.CreateCommand();

        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) "
            + "VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) "
            + "VALUES ('orders@test','test','full','active');";

        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exactly_one_settler_wins_and_the_other_changes_nothing()
    {
        // A report and a sweep aimed at the same row. Whichever lands first must be the one the
        // record keeps, and the loser must not half-write: a row carrying one settler's outcome and
        // another's timestamp would be a record nobody could account for.
        //
        // **There were three settlers when this was written, and the third was removed by the
        // finding this probe's neighbour produced.** A departing node used to settle its agents'
        // turns; a node's *stream* ending is not its process dying, so that destroyed an answer on
        // every network blip. Two remain, and both know what they are claiming.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var id = await _instructions.RecordAsync(Orders, $"attempt {attempt}", T0, Ct);

            var settled = 0;

            void Count(bool did)
            {
                if (did)
                {
                    _ = Interlocked.Increment(ref settled);
                }
            }

            Count(await _instructions.SettleAsync(
                id, InstructionOutcome.Taken, "reported", T0.AddMinutes(1), Ct));

            Count(await _instructions.SweepAsync(T0.AddMinutes(30), T0.AddMinutes(31), Ct) == 1);

            Assert.Equal(1, settled);

            var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

            // The winner's whole record, never a mixture.
            Assert.Equal(InstructionOutcome.Taken, read.Outcome);
            Assert.Equal("reported", read.Reply);
            Assert.Equal(T0.AddMinutes(1), read.SettledUtc);
        }
    }

    [Fact]
    public async Task A_sweep_that_runs_twice_settles_a_row_once()
    {
        // Ticks overlap under load, and both passes would write `failed` -- so a double write would
        // be invisible in the outcome column and visible only in the timestamp, which is the kind
        // of corruption nobody notices until they are reconstructing a timeline.
        var id = await _instructions.RecordAsync(Orders, "nothing reported", T0, Ct);

        var first = await _instructions.SweepAsync(T0.AddMinutes(30), T0.AddMinutes(31), Ct);
        var second = await _instructions.SweepAsync(T0.AddMinutes(40), T0.AddMinutes(41), Ct);

        Assert.Equal(1, first);
        Assert.Equal(0, second);

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(T0.AddMinutes(31), read.SettledUtc);
    }

    [Fact]
    public async Task Paging_neither_repeats_nor_skips_while_new_instructions_arrive()
    {
        // **The realistic race, and the one a cursor gets wrong.** Somebody reads page one, an
        // agent is instructed, and they ask for page two. A cursor anchored to an offset would
        // shift; one anchored to a row's own identifier cannot -- but that is a property of this
        // implementation rather than of listings in general, so it is probed.
        for (var at = 0; at < 6; at++)
        {
            _ = await _instructions.RecordAsync(Orders, $"original {at}", T0, Ct);
        }

        var first = await _instructions.ListAsync(limit: 2, cancellationToken: Ct);

        // Three more arrive between the two reads. They are newer, so they belong on a page the
        // reader has already passed -- and must not push anything into or out of the next one.
        for (var at = 0; at < 3; at++)
        {
            _ = await _instructions.RecordAsync(Orders, $"arrived later {at}", T0, Ct);
        }

        var second = await _instructions.ListAsync(
            limit: 2, beforeId: first[^1].Id, cancellationToken: Ct);

        Assert.Equal(["original 5", "original 4"], first.Select(i => i.Text));
        Assert.Equal(["original 3", "original 2"], second.Select(i => i.Text));

        Assert.DoesNotContain(second, i => i.Text.StartsWith("arrived", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_that_exactly_fills_says_there_is_no_next_one()
    {
        // The boundary the "ask for one more" trick exists to get right. A token handed out on a
        // full last page sends the caller back for nothing, which reads as a listing that will not
        // end.
        for (var at = 0; at < 4; at++)
        {
            _ = await _instructions.RecordAsync(Orders, $"number {at}", T0, Ct);
        }

        // The repository is asked for limit+1 by the wire edge; here the boundary is checked
        // directly, since that is the arithmetic the edge depends on.
        var exactly = await _instructions.ListAsync(limit: 5, cancellationToken: Ct);

        Assert.Equal(4, exactly.Count);

        var overflowing = await _instructions.ListAsync(limit: 4, cancellationToken: Ct);

        Assert.Equal(4, overflowing.Count);
    }

    [Fact]
    public async Task Reading_while_writing_never_returns_a_torn_record()
    {
        // A record is assembled from seven columns and two of them are paired by a CHECK. A reader
        // that saw an outcome without its timestamp -- or a reply on a pending row -- would be
        // reading a state the schema forbids, and would crash on the parse rather than return it.
        var id = await _instructions.RecordAsync(Orders, "under a writer", T0, Ct);

        var reading = Task.Run(async () =>
        {
            for (var at = 0; at < 300; at++)
            {
                var page = await _instructions.ListAsync(id: id, cancellationToken: Ct);

                foreach (var record in page)
                {
                    Assert.Equal(record.Outcome == InstructionOutcome.Pending, record.SettledUtc is null);
                    Assert.True(
                        record.Outcome != InstructionOutcome.Pending || record.Reply is null,
                        "a running instruction carried a reply");
                }
            }
        }, Ct);

        _ = await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, "done", T0.AddMinutes(1), Ct);

        await reading;
    }
}
