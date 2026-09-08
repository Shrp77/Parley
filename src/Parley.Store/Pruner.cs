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
            -- Conversations with an exchange still running. This is the first half of the idle
            -- rule and it does real work: a discussion is not finished because its earliest
            -- thread went quiet.
            SELECT 'c:' || e.conversation_id AS grp
              FROM exchanges e
             WHERE e.conversation_id IS NOT NULL AND e.state <> 'CLOSED'
            UNION
            -- And exchanges that are their own group. This branch decides nothing the floor
            -- would not: a live exchange's messages are refused by DeleteStatement whether or
            -- not they reach it. What it does is keep them from reaching it -- every sweep of a
            -- deployment with open exchanges would otherwise batch up its live traffic, ask the
            -- database to delete it, and be told no.
            SELECT 'x:' || e.id
              FROM exchanges e
             WHERE e.state <> 'CLOSED'
        ),
        judged AS (
            SELECT g.id AS id,

                   (
                     -- The age cap, which overrides whatever the idle rule concludes.
                     g.created <= @NowSeconds - @MaximumAgeSeconds

                     -- The idle rule: every exchange in the group closed, and nothing added for
                     -- the idle period.
                     -- NOT EXISTS rather than NOT IN. The subquery cannot produce a NULL as it
                     -- stands, but if it ever did, NOT IN would evaluate to NULL for every row
                     -- and retention would silently stop -- a total failure that looks exactly
                     -- like a healthy deployment with nothing due.
                     OR (NOT EXISTS (SELECT 1 FROM unfinished u WHERE u.grp = g.grp)
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

    /// <summary>Reads every message the two rules of SPEC.md §9.4 release.</summary>
    /// <remarks>
    /// <para>
    /// The rules only. The floor is not evaluated here, and that is the single most important
    /// thing about this query: it has to be re-evaluated at the moment of deletion anyway,
    /// because this scan runs outside any transaction and a message can be delivered or an
    /// exchange resumed in between. A copy here would be a second statement of a rule that
    /// already lives in <see cref="DeleteStatement"/>, and the two would mask each other —
    /// removing either one on its own changed no observable behaviour and failed no test, which
    /// is how a safety check quietly becomes decoration.
    /// </para>
    /// <para>
    /// The identifiers come back rather than being deleted in place, which makes the delete that
    /// follows a series of primary-key lookups instead of a second walk of the log.
    /// </para>
    /// <para>
    /// Carrying identifiers across a gap where other writers are running is sound because
    /// <c>messages.id</c> is <c>AUTOINCREMENT</c>, which SQLite guarantees never to reuse. On a
    /// plain <c>INTEGER PRIMARY KEY</c> the highest rowid is reissued after a delete, so a
    /// message written between this scan and the delete could take an identifier this pass had
    /// already judged — and be removed by a decision made about a different message entirely.
    /// That is a dependency on the schema, not an implementation detail of it.
    /// </para>
    /// <para>
    /// That shape is measured rather than assumed, because assuming it got it backwards once
    /// already. Against two hundred thousand messages with nothing due, the arrangement this
    /// replaced — one transaction holding the classification and the delete together — took
    /// 0.7–1.5 seconds, and a second connection asking for the write lock throughout it waited
    /// 545 milliseconds and got two turns. This takes 0.3–0.7 for the same scan and that
    /// connection waits nothing at all, because none of it is under the lock.
    /// </para>
    /// <para>
    /// The one-set-based-statement version is faster at deleting in bulk, and that is the trade
    /// accepted here. "Nothing due" is what every sweep of every server does forever; bulk is
    /// what a first drain does once, and <see cref="MaxRemovalsPerPass"/> bounds that
    /// separately.
    /// </para>
    /// </remarks>
    private const string SpentRows =
        Judged +
        """

        SELECT id FROM judged WHERE spent = 1;
        """;

    /// <summary>
    /// The most messages one pass will remove.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bound on a first drain rather than on ordinary work. Retention keeps a log small, so
    /// this is never reached twice — but the pass that reaches it once is the one run against a
    /// log that grew for months without a policy, and that pass is the dangerous one. Removing
    /// two hundred thousand messages took four seconds and grew the write-ahead log enough to
    /// force a checkpoint, and a concurrent writer measured a single wait of 2.3 seconds against
    /// a <c>busy_timeout</c> of five. It did not fail. A log twice that size, or a slower disk,
    /// and it would — as <c>SQLITE_BUSY</c> reaching an agent that asked a question.
    /// </para>
    /// <para>
    /// At ten thousand, measured the same way, the worst that writer waits is 200 milliseconds.
    /// The remainder is not lost: retention is not a deadline, so the next sweep continues where
    /// this one stopped and a backlog drains over a few of them. That is the whole reason a cap
    /// is available here and would not be on the reaper — an exchange past its deadline has
    /// somebody waiting on it, and a message past its retention has nobody.
    /// </para>
    /// <para>
    /// It does assume removal outruns production, which is ten thousand an hour against a
    /// protocol whose exchanges are one per pair and whose messages are capped at a few thousand
    /// characters. The exception is <c>Notify</c>, which has no guard of its own until SPEC.md
    /// §13's per-sender rate limits exist — a sender producing faster than this would keep a log
    /// growing under a policy that was applying correctly. That is the same gap §13 already
    /// names, reached from here, rather than one this cap introduces.
    /// </para>
    /// <para>
    /// Private, and the pass reports <see cref="PruneResult.Capped"/> rather than the number.
    /// A caller comparing its own count against a published constant is a caller that can get
    /// the comparison wrong, and it would put a storage tuning decision into the vocabulary of
    /// whatever renders it.
    /// </para>
    /// </remarks>
    private const int MaxRemovalsPerPass = 10_000;

    /// <summary>
    /// How many identifiers one <c>DELETE</c> carries.
    /// </summary>
    /// <remarks>
    /// SQLite bounds how many parameters a statement may bind, and Dapper expands a list into one
    /// parameter each. Five hundred is comfortably inside the smallest limit any build ships
    /// with, and the batching costs nothing: every statement is a primary-key lookup, and they
    /// all run inside the one transaction, so a chunked delete and a single delete commit
    /// identically.
    /// </remarks>
    private const int DeleteBatch = 500;

    /// <summary>Counts how many of a batch are still there.</summary>
    /// <remarks>
    /// Run in the same transaction as the delete, so the difference between what was asked for,
    /// what went, and what remains is exact. A row still present was refused by the floor; a row
    /// neither deleted nor present was removed by another sweep between this one deciding and
    /// doing. Both are ordinary and they mean completely different things, so the alternative —
    /// reporting the difference as one number — makes the common case read as the alarming one.
    /// </remarks>
    private const string SurvivorCount = "SELECT count(*) FROM messages WHERE id IN @Ids;";

    /// <summary>Removes a batch by identifier, subject to the floor of SPEC.md §9.4.</summary>
    /// <remarks>
    /// <para>
    /// <b>The floor lives here and nowhere else.</b> It is evaluated at the moment of deletion,
    /// against the row as it stands then, which is the only moment where being right about it
    /// matters: the scan that chose these identifiers ran outside any transaction, so a message
    /// may have been delivered or an orphan resumed since. Every clause reads the row itself or
    /// its exchange by primary key, so it costs almost nothing to ask again.
    /// </para>
    /// <para>
    /// The idle rule is deliberately not re-checked, and could not be without re-running the
    /// scan. What could have changed is that a conversation idle for days received a message in
    /// the milliseconds since — and these messages were prunable at the instant the sweep looked,
    /// exactly as they would have been to a sweep that ran a second earlier. That is not a loss
    /// the policy did not already sanction. The floor clauses are the ones where the answer
    /// genuinely differs, and those are here.
    /// </para>
    /// </remarks>
    private const string DeleteStatement =
        """
        DELETE FROM messages
         WHERE id IN @Ids

           -- Absolute: an exchange still open or orphaned may be replayed by section 14, and
           -- the question text is what there would be to replay. Bounded on its own, since
           -- ghosting closes an orphan, so nothing needs to override it.
           AND NOT EXISTS (SELECT 1 FROM exchanges x
                            WHERE x.id = messages.exchange_id
                              AND x.state IN ('OPEN', 'ORPHANED'))

           -- Absolute: inside the deduplication window of section 13.1, where the row is still
           -- doing work. Removing one there would let a byte-identical message through, so an
           -- operator tightening retention could break deduplication as a side effect -- the
           -- exact thing the floor exists to stop. The bucketed backstop index needs no separate
           -- allowance: a bucket is the same length as the window, so a row older than the
           -- window is always in a strictly older bucket and constrains no insert that could
           -- still arrive.
           AND unixepoch(created_utc) <= @NowSeconds - @DeduplicationSeconds

           -- Not absolute: an undelivered message is somebody's inbox, and nothing ever clears
           -- an inbox nobody reads. An agent that never attaches never collects, so this is the
           -- one clause with no terminal state -- and the age cap reaches it, or the cap is not
           -- a cap.
           AND (delivered_utc IS NOT NULL
                OR unixepoch(created_utc) <= @NowSeconds - @MaximumAgeSeconds);
        """;

    /// <summary>
    /// Removes instructions past the age cap, whatever became of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own statement rather than a clause on the message delete, and the separation is the
    /// point.</b> That statement carries three floors — an exchange that may still be replayed,
    /// the deduplication window, an inbox nobody has read — and not one of them means anything for
    /// an instruction. Sharing it would carry predicates that are always true here and invite the
    /// next reader to work out whether they matter.
    /// </para>
    /// <para>
    /// <b>Aged from <c>issued_utc</c>, and this is the trap worth naming.</b> The schema pairs a
    /// settled outcome with a settled instant, so a row still running has none by construction. A
    /// rule measured from settlement would therefore never reach the rows nothing ever settled —
    /// which is exactly the set most in need of a bound. An unbounded table can hide inside a
    /// retention policy that looks complete.
    /// </para>
    /// <para>
    /// No floor, because there is nothing an instruction is still doing at the age cap. It is not
    /// replayed, it is not deduplicated against, and nobody is waiting to collect it: §11.5 makes
    /// delivery the node's, so an instruction that was going to be delivered has been. What is left
    /// is a record, and §9.4's argument against a permanent record applies to it exactly as it
    /// applies to traffic.
    /// </para>
    /// <para>
    /// Unbatched, unlike the message delete. That one batches because it walks a log of hundreds of
    /// thousands; instructions are typed by a person, one at a time, and a deployment that has
    /// enough of them to need batching has a different problem.
    /// </para>
    /// </remarks>
    private const string DeleteInstructions =
        """
        DELETE FROM instructions
         WHERE unixepoch(issued_utc) <= @NowSeconds - @MaximumAgeSeconds;
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Removes every message the policy releases, and reports what the floor held back.
    /// </summary>
    /// <param name="policy">The retention policy to apply.</param>
    /// <param name="nowUtc">The instant to measure ages against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many were removed, how many the floor kept, and how many another sweep had.</returns>
    /// <seealso cref="MaxRemovalsPerPass"/>
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

        var parameters = new Windows(
            nowUtc.ToUnixTimeSeconds(),
            (long)MessageIdentity.DeduplicationWindow.TotalSeconds,
            (long)policy.IdlePeriod.TotalSeconds,
            (long)policy.MaximumAge.TotalSeconds);

        // Outside any transaction, and that is the point rather than an omission. A single
        // statement is atomic in SQLite, so it needs none for consistency — and taking one would
        // mean holding the IMMEDIATE lock admission needs for the whole walk of the log. On two
        // hundred thousand messages that walk is half a second and a drain is several, against a
        // busy_timeout of five: large enough, and questions would fail outright rather than wait.
        var spent = await _connection.QueryAsync<long>(
            new CommandDefinition(
                SpentRows, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var prunable = spent as List<long> ?? [.. spent];

        var capped = prunable.Count > MaxRemovalsPerPass;

        if (capped)
        {
            prunable.RemoveRange(
                MaxRemovalsPerPass, prunable.Count - MaxRemovalsPerPass);
        }

        var removed = 0;
        var held = 0;
        var contended = 0;

        for (var offset = 0; offset < prunable.Count; offset += DeleteBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = prunable.GetRange(offset, Math.Min(DeleteBatch, prunable.Count - offset));

            // One transaction per batch, so the lock is held for a few primary-key deletes at a
            // time. A pass interrupted halfway leaves the remainder exactly as the next sweep
            // would find it anyway: pruning is idempotent, and there is no state that is only
            // correct once all of it has been written. The reaper reaches the same arrangement
            // from the same reasoning.
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

            // Present but not deleted means the floor refused it a moment after releasing it.
            // Neither deleted nor present means another sweep had it first.
            held += surviving;
            contended += batch.Count - went - surviving;
        }

        // After the messages, and outside their batching. One statement, one lock, and it is
        // ordinary for it to remove nothing: instructions are issued by hand.
        using var instructions = WriteScope.Begin(_connection, ambient: null);

        var instructionsRemoved = await _connection.ExecuteAsync(
            new CommandDefinition(
                DeleteInstructions,
                parameters,
                instructions.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        instructions.Complete();

        return new PruneResult(removed, held, contended, capped, instructionsRemoved);
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
    /// <param name="NowSeconds">The instant of the sweep, as a Unix timestamp.</param>
    /// <param name="DeduplicationSeconds">The window of SPEC.md §13.1.</param>
    /// <param name="IdleSeconds">The idle period of §9.4.</param>
    /// <param name="MaximumAgeSeconds">The age cap of §9.4.</param>
    /// <remarks>
    /// Named rather than anonymous because the delete binds a subset of it, and two anonymous
    /// types that had to agree on four parameter names is the kind of agreement nothing checks.
    /// </remarks>
    private sealed record Windows(
        long NowSeconds, long DeduplicationSeconds, long IdleSeconds, long MaximumAgeSeconds);

}
