using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// How many exchanges reached a given clarification depth.
/// </summary>
/// <param name="Depth">Clarifications the answerer requested.</param>
/// <param name="Exchanges">How many exchanges reached exactly that depth.</param>
public sealed record ClarifyDepthBucket(int Depth, long Exchanges);

/// <summary>
/// The evidence SPEC.md §17 question 4 asks for and has never had.
/// </summary>
/// <param name="Distribution">Exchanges by the depth they reached, ascending, zero included.</param>
/// <param name="ExchangesAtCap">
/// Exchanges whose depth equals the cap they were admitted under. This is the whole of what a
/// breach produces: SPEC.md §5.2 refuses the clarification and writes nothing, so there is no
/// broker-authored close to count. An earlier version of this report counted one, which could
/// never be anything but zero.
/// </param>
/// <param name="LowestCapInForce">The smallest cap any counted exchange carries.</param>
/// <param name="HighestCapInForce">The largest cap any counted exchange carries.</param>
/// <remarks>
/// The caps are 64-bit for a reason that is not symmetry with the counts. Rows written before
/// the bound of SPEC.md §9.3 existed carry no constraint at all, so one of them can hold a
/// value no <c>int</c> can. Narrowing it would wrap, and it would wrap in the one field whose
/// entire job is to say what bound produced the distribution beside it.
/// </remarks>
/// <param name="Answers">Answers sent, whatever they resolved to.</param>
/// <param name="AnswersResolvingSubQuestionsDifferently">
/// Answers whose sub-questions did not all resolve the same way.
/// </param>
/// <remarks>
/// <para>
/// Every figure here is read from columns that already existed. Unlike the size cap, whose
/// refusals were discarded and had to be given a table of their own, clarification depth is
/// recorded on a row that outlives the exchange - so this needed no schema change, and whatever
/// history a deployment has already accumulated is readable.
/// </para>
/// <para>
/// <see cref="LowestCapInForce"/> and <see cref="HighestCapInForce"/> are reported because the
/// distribution is meaningless without them. A cap censors its own evidence: depths pile up at
/// whatever bound was in force, whether the true need was one more or forty more. Reading a
/// distribution without knowing where it was truncated is how a censored measurement gets
/// mistaken for a natural one, and the method §17 question 4 proposes is to raise the cap until
/// the pile disappears.
/// </para>
/// <para>
/// <see cref="AnswersResolvingSubQuestionsDifferently"/> is the cheap test of whether
/// sub-questions carry information at all. §17 question 4 argues the depth counter conflates
/// repeated clarification of one point with single requests for several different facts, and that
/// a per-sub-question budget would separate them. That is only worth building if answers ever
/// treat sub-questions as genuinely distinct. If every answer resolves all of them identically,
/// the structure is formatting and the proposal is dead.
/// </para>
/// <para>
/// Counts are 64-bit, depth is not. The schema bounds a depth and nothing bounds a count, and
/// this project has already had an operator report throw <c>OverflowException</c> permanently
/// because a running total was declared as an <c>int</c>.
/// </para>
/// </remarks>
public sealed record ClarifyReport(
    IReadOnlyList<ClarifyDepthBucket> Distribution,
    long ExchangesAtCap,
    long LowestCapInForce,
    long HighestCapInForce,
    long Answers,
    long AnswersResolvingSubQuestionsDifferently)
{
    /// <summary>Gets how many exchanges the distribution covers.</summary>
    public long Exchanges => Distribution?.Sum(b => b.Exchanges) ?? 0;

    /// <summary>Gets the deepest clarification any exchange reached, or zero if none did.</summary>
    public int DeepestReached =>
        Distribution is { Count: > 0 } d ? d.Max(b => b.Depth) : 0;
}

/// <summary>
/// Reads what clarification actually cost, for the operator report of SPEC.md §10.2.
/// </summary>
/// <param name="connection">The open, migrated database.</param>
/// <remarks>
/// Read-only by construction: it holds no write path at all. The report exists to inform a
/// decision about the cap, and a type that could also change something would put the measurement
/// and the intervention in one place, which is how a measurement stops being one.
/// </remarks>
public sealed class ClarifyReportRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    /// <summary>Reads the whole report.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The report. Every count is zero when nothing has been asked yet.</returns>
    /// <remarks>
    /// <para>
    /// Four statements rather than one. They answer different questions over different tables,
    /// and a single query joining exchanges to messages would multiply the exchange rows by the
    /// messages on them - which is exactly the shape of bug that makes a report look plausible
    /// and count wrong.
    /// </para>
    /// <para>
    /// Read through the raw reader rather than through Dapper, which is the exception in this
    /// assembly and so needs its reason recorded. Every column here is an aggregate, and SQLite
    /// gives an aggregate no declared type - so the provider infers one from the data. Over an
    /// empty table there is no data to infer from, <c>count(*)</c> arrives described as a BLOB,
    /// and the mapping fails. That is specific to the case where nothing has happened yet, which
    /// is exactly the state a new deployment is in when somebody first runs this. Reading by
    /// position and asking for integers sidesteps the inference.
    /// </para>
    /// </remarks>
    public async Task<ClarifyReport> ReadAsync(CancellationToken cancellationToken = default)
    {
        // One snapshot across all four statements. They are read separately to avoid a join
        // multiplying exchanges by their messages, which leaves them free to straddle a write
        // on a live deployment -- and the visible symptom of that is an at-cap count larger
        // than the total it is counted from, which reads as a bug in the report rather than as
        // the race it is. Deferred, because nothing here writes.
        using var snapshot = _connection.BeginTransaction(deferred: true);

        // Depth zero is included on purpose. "How many exchanges never clarified at all" is the
        // single most decisive number here: if it is nearly all of them, the cap is not what is
        // keeping depth low and tuning it changes nothing.
        var buckets = new List<ClarifyDepthBucket>();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT clarify_depth, count(*)
                  FROM exchanges
                 GROUP BY clarify_depth
                 ORDER BY clarify_depth;
                """;

            using var reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                buckets.Add(new ClarifyDepthBucket(reader.GetInt32(0), reader.GetInt64(1)));
            }
        }

        long lowestCap;
        long highestCap;
        long atCap;

        // A cap of zero is excluded from the at-cap count because every such exchange trivially
        // satisfies it at depth zero, which would report the strictest possible setting as
        // universally breached rather than as never approached.
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT coalesce(min(clarify_depth_cap), 0),
                       coalesce(max(clarify_depth_cap), 0),
                       coalesce(sum(clarify_depth >= clarify_depth_cap
                                    AND clarify_depth_cap > 0), 0)
                  FROM exchanges;
                """;

            using var reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            lowestCap = reader.GetInt64(0);
            highestCap = reader.GetInt64(1);
            atCap = reader.GetInt64(2);
        }

        long answers;
        long mixed;

        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT count(*),
                       coalesce(sum(
                         (SELECT count(DISTINCT json_extract(value, '$.resolution'))
                            FROM json_each(m.payload_json, '$.resolutions')) > 1), 0)
                  FROM messages m
                 WHERE m.type = 'answer'
                   AND json_type(m.payload_json, '$.resolutions') = 'array';
                """;

            using var reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            answers = reader.GetInt64(0);
            mixed = reader.GetInt64(1);
        }

        return new ClarifyReport(
            buckets, atCap, lowestCap, highestCap, answers, mixed);
    }
}
