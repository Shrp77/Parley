using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// How often one agent asked another and was told it was somebody else's.
/// </summary>
/// <param name="Asker">The agent that asked.</param>
/// <param name="Answerer">The agent it asked.</param>
/// <param name="Closed">Exchanges between them that reached a resolution.</param>
/// <param name="OutOfScope">How many of those resolved as not the answerer's.</param>
public sealed record ReferralPair(string Asker, string Answerer, long Closed, long OutOfScope)
{
    /// <summary>Gets the share of closed exchanges the answerer declined as not its own.</summary>
    public double Rate => Closed == 0 ? 0 : (double)OutOfScope / Closed;
}

/// <summary>
/// Where an answerer sent an asker instead.
/// </summary>
/// <param name="Asker">The agent that asked.</param>
/// <param name="Answerer">The agent that referred it onward.</param>
/// <param name="RedirectedTo">The agent named as the owner.</param>
/// <param name="Referrals">How many sub-questions went that way.</param>
/// <param name="LastUtc">When the most recent one was sent.</param>
public sealed record ReferralTarget(
    string Asker, string Answerer, string RedirectedTo, long Referrals, string LastUtc);

/// <summary>
/// The <c>out_of_scope</c> rate per pair of SPEC.md §15, and who the work was sent to.
/// </summary>
/// <param name="Pairs">Pairs where at least one exchange resolved out of scope.</param>
/// <param name="Targets">Who was named, per pair, most-referred first.</param>
/// <remarks>
/// <para>
/// §15 calls this the signal that the ownership map is wrong somewhere, and the two halves come
/// from different tables for a reason worth knowing when reading it. The rate is computed from
/// <c>exchanges</c>, which retention never touches, so it is accurate over the whole life of the
/// deployment. The targets come from <c>message_referrals</c>, which exists precisely because the
/// names used to live in a message payload that §9.4 deletes.
/// </para>
/// <para>
/// A high rate is not a fault in the answerer. It says two agents disagree about where a boundary
/// runs, or that one of them has a stale idea of what the other owns — which is a question for
/// the people who own those repositories, and exactly what an <c>owns</c> line is for.
/// </para>
/// </remarks>
public sealed record ReferralReport(
    IReadOnlyList<ReferralPair> Pairs,
    IReadOnlyList<ReferralTarget> Targets)
{
    /// <summary>Gets whether anything has been referred at all.</summary>
    public bool IsEmpty => (Pairs is null || Pairs.Count == 0)
                           && (Targets is null || Targets.Count == 0);
}

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
