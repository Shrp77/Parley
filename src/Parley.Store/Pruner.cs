using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Applies the retention policy of SPEC.md §9.4 to the message log.
/// </summary>
/// <remarks>
/// <para>
/// Parley is a channel rather than a store (§16), and this is what makes that true of the file rather
/// than only of the design. The log carries the full text of every request and result between
/// participants, so a log that grows forever grows the blast radius of a leaked database with it.
/// </para>
/// <para>
/// Messages are the only target. Exchange rows, <c>result_items</c> and <c>result_sources</c> carry no
/// free prose and are what §15's telemetry is computed from, so they outlive the messages they once
/// carried. Conversation rows stay because exchanges reference them.
/// </para>
/// </remarks>
/// <param name="connection">The connection to prune on.</param>
public sealed class Pruner(SqliteConnection connection)
{
    /// <summary>Classifies every message against the two rules, once.</summary>
    /// <remarks>
    /// <c>grp</c> is the unit §9.4's idle rule is evaluated over, which is the conversation: every
    /// message belongs to one (§5.0). <c>unfinished</c> is "some exchange in this conversation is still
    /// open", the first half of the idle rule — a line of work is not finished because its earliest
    /// exchange went quiet.
    /// </remarks>
    private const string Judged =
        """
        WITH grouped AS (
            SELECT m.id                     AS id,
                   unixepoch(m.created_utc) AS created,
                   m.conversation_id        AS grp
              FROM messages m
        ),
        activity AS (
            SELECT grp, max(created) AS last_activity
              FROM grouped
             GROUP BY grp
        ),
        unfinished AS (
            SELECT DISTINCT conversation_id AS grp
              FROM exchanges
             WHERE state <> 'CLOSED'
        ),
        judged AS (
            SELECT g.id AS id,

                   (
                     -- The age cap, which overrides whatever the idle rule concludes. The floor in
                     -- DeleteStatement still holds anything open or undelivered-and-young.
                     g.created <= @NowSeconds - @MaximumAgeSeconds

                     -- The idle rule: every exchange in the conversation closed, and nothing added
                     -- for the idle period. NOT EXISTS rather than NOT IN, because NOT IN over a
                     -- subquery that ever produced a NULL would silently stop retention. A closed
                     -- conversation waits out the period too: agents close one by consensus with no
                     -- person present, and the person must still be able to read it back.
                     OR (NOT EXISTS (SELECT 1 FROM unfinished u WHERE u.grp = g.grp)
                         AND a.last_activity <= @NowSeconds - @IdleSeconds)
                   ) AS spent

              FROM grouped g
              JOIN activity a ON a.grp = g.grp
        )
        """;

    /// <summary>Reads every message the two rules of §9.4 release.</summary>
    /// <remarks>
    /// <para>
    /// The rules only. The floor is not evaluated here, because it has to be re-evaluated at the moment
    /// of deletion anyway: this scan runs outside any transaction, and a message can be delivered or an
    /// exchange opened in a conversation in between. A copy here would mask the one that matters.
    /// </para>
    /// <para>
    /// Carrying identifiers across that gap is sound because <c>messages.id</c> is
    /// <c>AUTOINCREMENT</c>, which SQLite never reuses — on a plain <c>INTEGER PRIMARY KEY</c> a message
    /// written between the scan and the delete could take an identifier this pass had already judged.
    /// </para>
    /// <para>
    /// Measured rather than assumed: against two hundred thousand messages with nothing due, a scan
    /// outside the lock lets a concurrent writer wait nothing at all, where holding the lock for the
    /// scan made it wait over half a second.
    /// </para>
    /// </remarks>
    private const string SpentRows =
        Judged +
        """

        SELECT id FROM judged WHERE spent = 1;
        """;

    /// <summary>The most messages one pass will remove.</summary>
    /// <remarks>
    /// A bound on a first drain rather than on ordinary work: removing two hundred thousand messages in
    /// one pass made a concurrent writer wait 2.3 seconds against a five-second busy timeout. At ten
    /// thousand the worst wait measured 200 milliseconds, and the remainder is not lost — retention is
    /// not a deadline, and the next retention pass continues where this one stopped.
    /// </remarks>
    private const int MaxRemovalsPerPass = 10_000;

    /// <summary>How many identifiers one <c>DELETE</c> carries.</summary>
    /// <remarks>
    /// SQLite bounds how many parameters a statement may bind and Dapper expands a list into one each;
    /// five hundred is inside the smallest limit any build ships with.
    /// </remarks>
    private const int DeleteBatch = 500;

    /// <summary>Counts how many of a batch are still there.</summary>
    /// <remarks>
    /// In the same transaction as the delete, so what was asked for, what went and what remains are
    /// exact. Still present means the floor refused it; neither deleted nor present means another sweep
    /// had it first. Reporting the two as one number would make the common case read as the alarming one.
    /// </remarks>
    private const string SurvivorCount = "SELECT count(*) FROM messages WHERE id IN @Ids;";

    /// <summary>Removes a batch by identifier, subject to the floor of §9.4.</summary>
    /// <remarks>
    /// <b>The floor lives here and nowhere else</b>, evaluated against each row as it stands at the
    /// moment of deletion, which is the only moment being right about it matters.
    /// </remarks>
    private const string DeleteStatement =
        """
        DELETE FROM messages
         WHERE id IN @Ids

           -- Absolute: work still owed. Nothing ends a piece of work but its participants or its
           -- person (principle 2.8), so nothing deletes its text on a schedule either. What bounds
           -- this is the flag of section 6 putting stalled work in front of the person, who closes it.
           AND NOT EXISTS (SELECT 1 FROM exchanges x
                            WHERE x.id = messages.exchange_id
                              AND x.state = 'OPEN')

           -- Absolute: inside the deduplication window of section 13.1, where the row is still doing
           -- work. A bucket is the same length as the window, so a row older than the window is in a
           -- strictly older bucket and constrains no insert that could still arrive.
           AND unixepoch(created_utc) <= @NowSeconds - @DeduplicationSeconds

           -- Not absolute: an undelivered message is somebody's inbox, and nothing clears an inbox
           -- nobody reads. The age cap reaches it, or the cap is not a cap.
           AND (delivered_utc IS NOT NULL
                OR unixepoch(created_utc) <= @NowSeconds - @MaximumAgeSeconds)

           -- Not absolute either: the Request an undelivered Result carries back (section 4.2). Without
           -- it, a requester away for longer than the idle period collected a Result with nothing to say
           -- what it answered. Measured on the Result, so the Request goes when the age cap reaches the
           -- Result and not before -- the Request is always the older, and would otherwise age out first.
           AND NOT (type = 'request'
                    AND EXISTS (SELECT 1 FROM messages r
                                 WHERE r.exchange_id = messages.exchange_id
                                   AND r.type = 'result'
                                   AND r.delivered_utc IS NULL
                                   AND unixepoch(r.created_utc) > @NowSeconds - @MaximumAgeSeconds));
        """;

    /// <summary>Removes what became of proposals once it is old: decided proposals and delivered notices.</summary>
    /// <remarks>
    /// <para>
    /// <b>A roadmap, its items and a waiting proposal are never removed here</b> (SPEC.md §9.4). Retention is a
    /// bound on traffic, and a roadmap is not traffic: it is the list the person kept so that it would not be lost
    /// in one. A waiting proposal is work waiting on the person, and removing it would be deciding for them.
    /// </para>
    /// <para>
    /// A roadmap that came of a removed proposal keeps its identifier and its proposer's name as text, so nothing
    /// is lost by it. Its own statement, because none of the message
    /// floors means anything for rows that are not messages.
    /// </para>
    /// </remarks>
    private const string DeleteDecidedProposals =
        """
        DELETE FROM roadmap_notices
         WHERE delivered_utc IS NOT NULL
           AND unixepoch(delivered_utc) <= @NowSeconds - @MaximumAgeSeconds;

        DELETE FROM roadmap_proposals
         WHERE state <> 'waiting'
           AND unixepoch(decided_utc) <= @NowSeconds - @MaximumAgeSeconds;
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Removes every message the policy releases, and reports what the floor held back.</summary>
    /// <param name="policy">The retention policy to apply.</param>
    /// <param name="nowUtc">The instant to measure ages against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many were removed, how many the floor kept, and how many another sweep had.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is unusable.</exception>
    /// <remarks>
    /// The held count is the answer to "why did that prune less than I expected": nothing old enough, and
    /// plenty old enough but held by the floor, are otherwise indistinguishable.
    /// </remarks>
    public async Task<PruneResult> PruneAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var parameters = new Windows(
            nowUtc.ToUnixTimeSeconds(),
            (long)MessageIdentity.DeduplicationWindow.TotalSeconds,
            (long)policy.IdlePeriod.TotalSeconds,
            (long)policy.MaximumAge.TotalSeconds);

        // Outside any transaction: a single statement is atomic, and taking the IMMEDIATE lock
        // admission needs for the whole walk of the log would make Requests wait on it.
        var spent = await _connection.QueryAsync<long>(
            new CommandDefinition(
                SpentRows, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var prunable = spent as List<long> ?? [.. spent];

        var capped = prunable.Count > MaxRemovalsPerPass;

        if (capped)
        {
            prunable.RemoveRange(MaxRemovalsPerPass, prunable.Count - MaxRemovalsPerPass);
        }

        var removed = 0;
        var held = 0;
        var contended = 0;

        for (var offset = 0; offset < prunable.Count; offset += DeleteBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = prunable.GetRange(offset, Math.Min(DeleteBatch, prunable.Count - offset));

            // One transaction per batch, so the lock is held for a few primary-key deletes at a time.
            // Pruning is idempotent, so a pass interrupted halfway leaves what the next pass would find.
            using var scope = WriteScope.Begin(_connection, ambient: null);

            var went = await _connection.ExecuteAsync(
                new CommandDefinition(
                    DeleteStatement, Bind(batch, parameters), scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            var surviving = await _connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    SurvivorCount, new { Ids = batch }, scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            scope.Complete();

            removed += went;
            held += surviving;
            contended += batch.Count - went - surviving;
        }

        using (var proposals = WriteScope.Begin(_connection, ambient: null))
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    DeleteDecidedProposals,
                    parameters,
                    proposals.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            proposals.Complete();
        }

        ParleyDatabase.Flush(_connection);

        return new PruneResult(removed, held, contended, capped, policy);
    }

    /// <summary>Binds a batch of identifiers alongside the windows the floor is re-checked on.</summary>
    /// <param name="ids">The identifiers to delete.</param>
    /// <param name="windows">The windows.</param>
    /// <returns>The parameter object for one delete.</returns>
    private static object Bind(IReadOnlyList<long> ids, Windows windows) => new
    {
        Ids = ids,
        windows.NowSeconds,
        windows.DeduplicationSeconds,
        windows.MaximumAgeSeconds
    };

    /// <summary>The instants and durations the statements are evaluated against.</summary>
    /// <param name="NowSeconds">The instant of the pass, as a Unix timestamp.</param>
    /// <param name="DeduplicationSeconds">The window of §13.1.</param>
    /// <param name="IdleSeconds">The idle period of §9.4.</param>
    /// <param name="MaximumAgeSeconds">The age cap of §9.4.</param>
    private sealed record Windows(
        long NowSeconds, long DeduplicationSeconds, long IdleSeconds, long MaximumAgeSeconds);
}
