using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Reads what clarification actually cost, for the operator report of SPEC.md §10.2.
/// </summary>
/// <param name="connection">The open, migrated database.</param>
/// <remarks>
/// Read-only by construction. The report informs a decision about a cap, and a type that could also
/// change something would put the measurement and the intervention in one place.
/// </remarks>
public sealed class ClarifyReportRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    /// <summary>Reads the whole report.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The report. Every count is zero when nothing has happened yet.</returns>
    /// <remarks>
    /// <para>
    /// Separate statements rather than one join, because a join of exchanges to their Results would
    /// multiply rows and count wrong while looking plausible. One snapshot across all of them, so they
    /// cannot straddle a write.
    /// </para>
    /// <para>
    /// Read through the raw reader rather than Dapper, which is the exception in this assembly: every
    /// column is an aggregate, SQLite gives an aggregate no declared type, and over an empty table the
    /// provider infers a BLOB and the mapping fails — on exactly the new deployment somebody first runs
    /// this against.
    /// </para>
    /// <para>
    /// Results are counted from <c>result_items</c> rather than from the messages, so the count survives
    /// retention (§9.3).
    /// </para>
    /// </remarks>
    public async Task<ClarifyReport> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var snapshot = _connection.BeginTransaction(deferred: true);

        // Depth zero is included on purpose: "how many exchanges never needed a clarification" is the
        // number that says whether a cap would change anything at all.
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

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                buckets.Add(new ClarifyDepthBucket(reader.GetInt32(0), reader.GetInt64(1)));
            }
        }

        long lowestCap;
        long highestCap;
        long atCap;

        // A cap of zero is excluded from the at-cap count because every such exchange trivially
        // satisfies it at depth zero, which would report the strictest setting as universally breached.
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

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            lowestCap = reader.GetInt64(0);
            highestCap = reader.GetInt64(1);
            atCap = reader.GetInt64(2);
        }

        long results;
        long mixed;

        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT count(*), coalesce(sum(outcomes > 1), 0)
                  FROM (SELECT count(DISTINCT outcome) AS outcomes
                          FROM result_items
                         GROUP BY message_id);
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            results = reader.GetInt64(0);
            mixed = reader.GetInt64(1);
        }

        return new ClarifyReport(buckets, atCap, lowestCap, highestCap, results, mixed);
    }
}
