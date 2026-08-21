using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Applies the retention policy of SPEC.md §9.4 to the message log.
/// </summary>
/// <remarks>
/// <para>
/// Parley is a channel rather than a store (§16), and this is what makes that true of the file
/// rather than only of the design. The log carries the full text of every question and answer
/// between agents, so a log that grows forever grows the blast radius of a leaked database
/// forever with it.
/// </para>
/// <para>
/// Separate from <see cref="Reaper"/> although both are swept on the same tick. The reaper moves
/// exchanges through their lifecycle and creates work — a ghost is a message it writes; this
/// removes text and creates none. Running them from one type would also put the deletion of a
/// question inside the same pass that reads one to build a ghost notice, which is an ordering
/// nobody should have to hold in their head.
/// </para>
/// <para>
/// Messages are the only target. Exchange rows carry no free prose and are what §15's telemetry
/// is computed from, so they outlive the messages they once carried — which is what lets a
/// deployment answer "how often does this pair disagree" a year later without keeping a year of
/// what they actually said. Conversation rows stay for the same reason, and because an exchange
/// still references them.
/// </para>
/// </remarks>
/// <param name="connection">The connection to prune on.</param>
public sealed class Pruner(SqliteConnection connection)
{
    /// <summary>
    /// Classifies every message against the floor and against the two rules, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>grp</c> is the unit §9.4's idle rule is evaluated over: the conversation where there is
    /// one, the exchange where a message stands alone, and the message itself for a notification
    /// that named neither. The identifiers are prefixed because an exchange id and a
    /// conversation id are different namespaces and concatenating without one would let an
    /// exchange numbered 7 share a group with a conversation called <c>7</c>.
    /// </para>
    /// <para>
    /// The conversation is read from the <em>exchange</em> in preference to the message. §9.2
    /// makes the exchange authoritative and the message's copy a convenience held in agreement
    /// by a composite foreign key, and that key is satisfied by a NULL on the message side — so
    /// a message may legitimately carry no identifier while its exchange does. Grouping by the
    /// message's copy would put such a message in a group of its own and prune it out from under
    /// a discussion still running.
    /// </para>
    /// <para>
    /// <c>unfinished</c> is "some exchange in this group has not closed", which is the first half
    /// of the idle rule. Note the second branch: an exchange with no conversation is its own
    /// group, so it must appear under its own key as well as under its conversation's.
    /// </para>
    /// </remarks>
    private const string Judged =
        """
        WITH grouped AS (
            SELECT m.id                     AS id,
                   unixepoch(m.created_utc) AS created,
                   m.delivered_utc          AS delivered,
                   m.exchange_id            AS exchange_id,
                   coalesce('c:' || coalesce(e.conversation_id, m.conversation_id),
                            'x:' || m.exchange_id,
                            'm:' || m.id)   AS grp
              FROM messages m
              LEFT JOIN exchanges e ON e.id = m.exchange_id
        ),
        activity AS (
            SELECT grp, max(created) AS last_activity
              FROM grouped
             GROUP BY grp
        ),
        unfinished AS (
            SELECT 'c:' || e.conversation_id AS grp
              FROM exchanges e
             WHERE e.conversation_id IS NOT NULL AND e.state <> 'CLOSED'
            UNION
            SELECT 'x:' || e.id
              FROM exchanges e
             WHERE e.state <> 'CLOSED'
        ),
        judged AS (
            SELECT g.id AS id,

                   -- The floor of SPEC.md 9.4. Two clauses are absolute and one is not, which
                   -- is the whole of the difference between them.
                   (
                     -- Absolute: an exchange that is still open or orphaned may be replayed by
                     -- section 14, and the question text is what there would be to replay. This
                     -- clause is bounded on its own -- ghosting closes an orphan -- so nothing
                     -- needs to override it.
                     NOT EXISTS (SELECT 1 FROM exchanges x
                                  WHERE x.id = g.exchange_id
                                    AND x.state IN ('OPEN', 'ORPHANED'))

                     -- Absolute: inside the deduplication window of section 13.1, where the row
                     -- is still doing work. Removing one there would let a byte-identical
                     -- message through, so an operator tightening retention could break
                     -- deduplication as a side effect -- the exact thing the floor exists to
                     -- stop. The bucketed backstop index needs no separate allowance: a bucket
                     -- is the same length as the window, so a row older than the window is
                     -- always in a strictly older bucket and constrains no insert that could
                     -- still arrive.
                     AND g.created <= @NowSeconds - @DeduplicationSeconds

                     -- Not absolute: an undelivered message is somebody's inbox, and nothing
                     -- ever clears an inbox nobody reads. An agent that never attaches never
                     -- collects, so this is the one clause with no terminal state -- and the
                     -- age cap reaches it, or the cap is not a cap.
                     AND (g.delivered IS NOT NULL
                          OR g.created <= @NowSeconds - @MaximumAgeSeconds)
                   ) AS clears_floor,

                   (
                     -- The age cap, which overrides whatever the idle rule concludes.
                     g.created <= @NowSeconds - @MaximumAgeSeconds

                     -- The idle rule: every exchange in the group closed, and nothing added for
                     -- the idle period.
                     OR (g.grp NOT IN (SELECT grp FROM unfinished)
                         AND (
                               -- Explicit closure short-circuits the idle period. The
                               -- participants have said the discussion is over, which is better
                               -- evidence than silence and arrives sooner.
                               EXISTS (SELECT 1 FROM conversations c
                                        WHERE 'c:' || c.id = g.grp AND c.state = 'CLOSED')
                               OR a.last_activity <= @NowSeconds - @IdleSeconds
                             ))
                   ) AS spent

              FROM grouped g
              JOIN activity a ON a.grp = g.grp
        )
        """;

    /// <summary>Removes what may go.</summary>
    /// <remarks>
    /// The <c>WITH</c> clause leads the <c>DELETE</c> rather than sitting inside a subquery, so
    /// the classification is evaluated once against the table as it stands. Repeating the
    /// predicate in the delete's own <c>WHERE</c> would have it read the very table being
    /// deleted from, and the set being removed would shift underneath the scan deciding what to
    /// remove.
    /// </remarks>
    private const string DeleteStatement =
        Judged +
        """

        DELETE FROM messages
         WHERE id IN (SELECT id FROM judged WHERE clears_floor = 1 AND spent = 1);
        """;

    /// <summary>Counts what the rules would have released and the floor is holding.</summary>
    private const string HeldCount =
        Judged +
        """

        SELECT count(*) FROM judged WHERE clears_floor = 0 AND spent = 1;
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Removes every message the policy releases, and reports what the floor held back.
    /// </summary>
    /// <param name="policy">The retention policy to apply.</param>
    /// <param name="nowUtc">The instant to measure ages against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many messages were removed, and how many the floor kept.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is unusable.</exception>
    /// <remarks>
    /// <para>
    /// One transaction covering both statements, unlike <see cref="Reaper.SweepAsync"/>, which
    /// deliberately writes each exchange on its own. The reasoning that makes independent writes
    /// right there does not apply here: the reaper's work is a series of unrelated decisions that
    /// admission control must be able to interleave with, while this is one decision about one
    /// set of rows, and the count it reports is only meaningful against the same snapshot the
    /// delete ran on.
    /// </para>
    /// <para>
    /// The held count is not diagnostic noise. It is the answer to "why did that prune less than
    /// I expected", and without it the two cases — nothing was old enough, and plenty was old
    /// enough but the floor is holding it — are indistinguishable from a single number.
    /// </para>
    /// <para>
    /// When §13's per-sender rate limits arrive they bring a window of their own, and §9.4 puts
    /// it in this floor beside the deduplication window. Nothing here needs to change except the
    /// clause: the shape is already "retain while the row is still doing operational work".
    /// </para>
    /// </remarks>
    public async Task<PruneResult> PruneAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var parameters = new
        {
            NowSeconds = nowUtc.ToUnixTimeSeconds(),
            DeduplicationSeconds = (long)MessageIdentity.DeduplicationWindow.TotalSeconds,
            IdleSeconds = (long)policy.IdlePeriod.TotalSeconds,
            MaximumAgeSeconds = (long)policy.MaximumAge.TotalSeconds
        };

        using var scope = WriteScope.Begin(_connection, ambient: null);

        var held = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                HeldCount, parameters, scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var removed = await _connection.ExecuteAsync(
            new CommandDefinition(
                DeleteStatement, parameters, scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();

        return new PruneResult(removed, held);
    }
}
