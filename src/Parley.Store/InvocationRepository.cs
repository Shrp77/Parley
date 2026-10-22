using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// An agent that owes an answer and has consented to being run.
/// </summary>
/// <param name="Agent">Who to start.</param>
/// <param name="Workspace">The directory it works in.</param>
/// <param name="Waiting">
/// How many things are waiting on it: exchanges it owes an answer, plus messages it has not
/// collected. Counted together because both are reasons to give an agent a turn and neither is
/// visible to the other — an answer to a question this agent asked leaves no debt at all.
/// </param>
/// <param name="Connected">Whether its own adapter is attached right now.</param>
/// <param name="StuckAt">
/// What was still waiting after this agent's most recent turn, if that turn changed nothing --
/// and null if it made progress or has never run. A trigger that does not expire needs this.
/// An owed exchange clears itself on §14's schedule whether or not the agent cooperates, but an
/// uncollected message does not: §9.4's floor holds it against retention precisely so nothing
/// is lost, so an agent that is started and does not collect is a candidate again on the next
/// pass, and every pass after that, each one a real session on somebody's account. Reported
/// rather than filtered on, like liveness, because whether to spend again on an agent that
/// spent and achieved nothing is the supervisor's judgement.
/// </param>
/// <remarks>
/// <para>
/// <b>No launch command.</b> The node on the agent's machine owns how to start it, and the
/// broker holds no executable command line at all — see SPEC.md §4. A candidate says who needs
/// a turn and how much is waiting; whether it can be started, and starting it, belong to the
/// machine it is on.
/// </para>
/// <para>
/// <see cref="Connected"/> is reported rather than filtered on. A supervisor that keeps agents
/// resident connects its own agents — they hold open sessions precisely because it started
/// them — so a query that excluded connected agents would return every candidate exactly once
/// and then never again. Who is connected and who is mine is a distinction only the supervisor
/// can draw, so it draws it.
/// </para>
/// </remarks>
public sealed record InvocationCandidate(
    AgentName Agent,
    string Workspace,
    long Waiting,
    bool Connected,
    long? StuckAt);

/// <summary>How an agent's most recent recorded turn ended (SPEC.md §11.6).</summary>
/// <param name="Agent">The agent.</param>
/// <param name="EndedUtc">When it ended: its start plus how long it ran.</param>
/// <param name="Clean">Whether it finished cleanly: exit code nought and not cut off.</param>
/// <param name="PermissionDenials">Tool calls it had refused for want of permission.</param>
public sealed record LatestTurn(AgentName Agent, DateTimeOffset EndedUtc, bool Clean, int PermissionDenials);

/// <summary>
/// Who can be started, and what starting them has cost.
/// </summary>
/// <param name="connection">The open, migrated database.</param>
/// <remarks>
/// Deliberately does not decide whether to invoke anybody. It answers "who is eligible" and
/// records "this is what happened"; the choice between them belongs to whatever hosts the sweep,
/// and keeping it there is what lets the policy change without touching storage.
/// </remarks>
public sealed class InvocationRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    /// <summary>
    /// Finds agents that owe an answer and may be run.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Who owes something, busiest first.</returns>
    /// <remarks>
    /// <para>
    /// Two conditions here, and the third is not this query's to apply. The agent must actually
    /// have something waiting — an exchange it owes, or a message nobody has collected — or the
    /// turn is tokens spent on nothing. And it must say where it works, because a turn is taken in
    /// a workspace.
    /// </para>
    /// <para>
    /// <b>Consent is the caller's question and lives in the node catalogue.</b> <c>AgentFleet</c>
    /// applies it by asking whether this machine has a launch command for the role.
    /// </para>
    /// <para>
    /// A retired agent is excluded by the status filter for the same reason §14 refuses to route
    /// to one: retirement is a person's judgement that the agent will not come back, and a
    /// process started against it would overrule that.
    /// </para>
    /// <para>
    /// Liveness is deliberately not among them — see <see cref="InvocationCandidate"/>. It is
    /// reported so the caller can refuse to start a second process behind an identity somebody
    /// else is already running, which is a judgement about who started what and therefore not
    /// one a query can make.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<InvocationCandidate>> FindWaitingAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<InvocationCandidate>();

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            WITH waiting(agent, item) AS (
                SELECT owed_by, 'x' || id FROM exchanges WHERE state = 'OPEN'
                UNION
                SELECT to_agent, 'm' || id FROM messages WHERE delivered_utc IS NULL
            )
            SELECT a.name, a.workspace,
                   count(*) AS waiting, a.connected,
                   (SELECT CASE WHEN i.waiting_after >= i.waiting_before
                                THEN i.waiting_after END
                      FROM invocations i
                     WHERE i.agent = a.name
                     ORDER BY i.id DESC
                     LIMIT 1) AS stuck_at
              FROM waiting w
              JOIN agents a ON a.name = w.agent
             WHERE a.status <> 'retired'
               AND a.workspace IS NOT NULL
             GROUP BY a.name, a.workspace, a.connected
             ORDER BY waiting DESC, a.name;
            """;

        using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new InvocationCandidate(
                AgentName.Create(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return candidates;
    }

    /// <summary>
    /// Finds every agent that must not be kept running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Agents a person has retired.</returns>
    /// <remarks>
    /// <para>
    /// Retiring an agent is §14's judgement that it will not come back, and it must reach a process
    /// that is already running: otherwise an agent already resident would keep answering under an
    /// identity its operator had withdrawn.
    /// </para>
    /// <para>
    /// Returns everyone withdrawn rather than taking the caller's list and filtering to it. The
    /// filtered form needed an <c>IN</c> clause built at runtime, and this repository treats
    /// composed SQL as a build error with exactly one justified exception. A registry is tens of
    /// rows; intersecting in the caller costs nothing and keeps every statement here a constant.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> FindWithdrawnAsync(
        CancellationToken cancellationToken = default)
    {
        var withdrawn = new List<string>();

        using var command = _connection.CreateCommand();
        command.CommandText =
            // Retirement alone. Permission to start an agent lives in the node's own catalogue,
            // which the broker never sees, so the only statement about an agent held in the store
            // is the operator's judgement.
            "SELECT name FROM agents WHERE status = 'retired';";

        using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            withdrawn.Add(reader.GetString(0));
        }

        return withdrawn;
    }

    /// <summary>Counts what an agent currently owes.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Open exchanges waiting on it.</returns>
    /// <remarks>
    /// Read before and after an invocation, so the record can say whether the process changed
    /// anything. An agent that ran and still owes exactly what it owed is the one worth noticing.
    /// </remarks>
    public async Task<long> CountWaitingAsync(
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*) FROM (
                SELECT 'x' || id AS item FROM exchanges
                 WHERE state = 'OPEN' AND owed_by = @Agent
                UNION
                SELECT 'm' || id FROM messages
                 WHERE delivered_utc IS NULL AND to_agent = @Agent
            );
            """;
        command.Parameters.AddWithValue("@Agent", agent.Value);

        using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? reader.GetInt64(0)
            : 0;
    }

    /// <summary>Records one invocation.</summary>
    /// <param name="agent">Who was started.</param>
    /// <param name="owedBefore">What it owed before.</param>
    /// <param name="owedAfter">What it owed after.</param>
    /// <param name="startedUtc">When it started.</param>
    /// <param name="elapsed">How long it ran.</param>
    /// <param name="exitCode">The turn's verdict: zero when it ran to a clean finish.</param>
    /// <param name="timedOut">Whether the supervisor gave up waiting for it.</param>
    /// <param name="costUsd">What the turn cost, as the runner reported it.</param>
    /// <param name="modelTurns">How many model turns went into it.</param>
    /// <param name="permissionDenials">How many tool calls were refused for want of permission.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Rows written.</returns>
    /// <remarks>
    /// <para>
    /// Every turn is recorded, including the ones that failed and the ones that achieved nothing.
    /// §17 question 1's decision to leave request volume unbounded rests on measuring it, and this is the
    /// same bargain on something that costs considerably more: recording only the successes would
    /// make a deployment burning tokens on failures look idle.
    /// </para>
    /// <para>
    /// <paramref name="exitCode"/> is the turn's verdict — zero for a clean finish — since a
    /// resident agent has no process exit to report.
    /// </para>
    /// </remarks>
    public async Task<int> RecordAsync(
        AgentName agent,
        long owedBefore,
        long owedAfter,
        DateTimeOffset startedUtc,
        TimeSpan elapsed,
        int exitCode,
        bool timedOut,
        double costUsd = 0,
        int modelTurns = 0,
        int permissionDenials = 0,
        CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO invocations
                (agent, waiting_before, waiting_after, started_utc, elapsed_ms, exit_code, timed_out,
                 cost_usd, model_turns, permission_denials)
            VALUES (@Agent, @Before, @After, @Started, @Elapsed, @Exit, @TimedOut,
                    @Cost, @Turns, @Denials);
            """;

        command.Parameters.AddWithValue("@Agent", agent.Value);
        command.Parameters.AddWithValue("@Before", owedBefore);
        command.Parameters.AddWithValue("@After", owedAfter);
        command.Parameters.AddWithValue("@Started", ExchangeRow.FormatTimestamp(startedUtc));
        command.Parameters.AddWithValue("@Elapsed", (long)elapsed.TotalMilliseconds);
        command.Parameters.AddWithValue("@Exit", exitCode);
        command.Parameters.AddWithValue("@TimedOut", timedOut ? 1 : 0);

        // Clamped rather than trusted. These three come from the runner's own accounting, and a
        // negative cost or turn count is a runner bug that would otherwise become a CHECK
        // violation -- turning somebody else's malformed number into this agent's failed write.
        command.Parameters.AddWithValue("@Cost", Math.Max(0, costUsd));
        command.Parameters.AddWithValue("@Turns", Math.Max(0, modelTurns));
        command.Parameters.AddWithValue("@Denials", Math.Max(0, permissionDenials));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads how each agent's most recent recorded turn ended.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One per agent that has a recorded turn.</returns>
    /// <remarks>
    /// <b>The most recently recorded, not the most recently started</b>: a node reports a turn when it ends,
    /// so the order rows arrive in is the order turns finished, and that is what "the last turn" means to somebody asking
    /// why an agent has stopped. The read the registry makes on every room refresh, so it is one statement.
    /// </remarks>
    public async Task<IReadOnlyList<LatestTurn>> LatestTurnsAsync(CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.agent, i.started_utc, i.elapsed_ms, i.exit_code, i.timed_out, i.permission_denials
              FROM invocations i
             WHERE i.id = (SELECT max(id) FROM invocations WHERE agent = i.agent);
            """;

        var latest = new List<LatestTurn>();

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!AgentName.TryCreate(reader.GetString(0), out var agent))
            {
                continue;
            }

            latest.Add(new LatestTurn(
                agent,
                EndOf(ExchangeRow.ParseTimestamp(reader.GetString(1)), reader.GetInt64(2)),
                reader.GetInt64(3) == 0 && reader.GetInt64(4) == 0,
                (int)reader.GetInt64(5)));
        }

        return latest;
    }

    /// <summary>When a turn ended: its start plus its length, held to the calendar.</summary>
    /// <param name="started">When it began.</param>
    /// <param name="elapsedMs">How long it ran.</param>
    /// <returns>The end, or the last representable instant.</returns>
    /// <remarks>
    /// A row is a node's say-so (§16), and an end past the year 9999 would make the addition throw on every
    /// read of the registry and of the flags, so the sum is clamped to the calendar.
    /// </remarks>
    internal static DateTimeOffset EndOf(DateTimeOffset started, long elapsedMs) =>
        elapsedMs <= 0
            ? started
            : elapsedMs >= (DateTimeOffset.MaxValue - started).TotalMilliseconds
                ? DateTimeOffset.MaxValue
                : started.AddMilliseconds(elapsedMs);
}
