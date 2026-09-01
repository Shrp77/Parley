using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;



/// <summary>
/// Reads where the ownership map is being argued about, for the operator report of §10.2.
/// </summary>
/// <param name="connection">The open, migrated database.</param>
/// <remarks>
/// Read-only, and an operator operation rather than an agent one for the same reason
/// <c>export</c> is: it reads across every pair in the deployment, so an agent holding it would
/// learn who is asking whom and how often it goes wrong — which §10.1 withholds.
/// </remarks>
public sealed class ReferralReportRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    /// <summary>Reads the whole report.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The report, empty when nothing has been referred.</returns>
    /// <remarks>
    /// Read through the raw reader rather than Dapper, for the reason recorded on
    /// <see cref="ClarifyReportRepository"/>: SQLite gives an aggregate no declared type, so over
    /// an empty table <c>count(*)</c> arrives described as a BLOB and the mapping fails — in
    /// exactly the state a new deployment is in when somebody first runs this.
    /// </remarks>
    public async Task<ReferralReport> ReadAsync(CancellationToken cancellationToken = default)
    {
        // One snapshot across both statements. They are counted from different tables and a write
        // landing between them would report targets a pair's own totals cannot account for.
        using var snapshot = _connection.BeginTransaction(deferred: true);

        var pairs = new List<ReferralPair>();

        // From exchanges, not from messages. Retention deletes messages and never deletes an
        // exchange, so this denominator is the whole history of the deployment rather than
        // whatever is left inside the retention window.
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT asker, answerer, count(*),
                       coalesce(sum(resolution = 'out_of_scope'), 0)
                  FROM exchanges
                 WHERE state = 'CLOSED'
                 GROUP BY asker, answerer
                HAVING coalesce(sum(resolution = 'out_of_scope'), 0) > 0
                 ORDER BY coalesce(sum(resolution = 'out_of_scope'), 0) DESC, asker, answerer;
                """;

            using var reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pairs.Add(new ReferralPair(
                    reader.GetString(0), reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3)));
            }
        }

        var targets = new List<ReferralTarget>();

        // from_agent is the answerer and to_agent the asker: the row records the direction the
        // referral travelled, which is the reverse of the direction the question did. Presented
        // asker-first to match the pairs above, because a report that swaps the order of two
        // names between its own halves is a report nobody can read.
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT to_agent, from_agent, redirected_to, count(*), max(created_utc)
                  FROM message_referrals
                 GROUP BY to_agent, from_agent, redirected_to
                 ORDER BY count(*) DESC, to_agent, from_agent, redirected_to;
                """;

            using var reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                targets.Add(new ReferralTarget(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), reader.GetString(4)));
            }
        }

        return new ReferralReport(pairs, targets);
    }
}
