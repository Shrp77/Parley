using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// An agent that owes an answer and has consented to being run.
/// </summary>
/// <param name="Agent">Who to start.</param>
/// <param name="Workspace">The directory it works in.</param>
/// <param name="InvokeCommand">The command its own launch configuration supplied.</param>
/// <param name="Waiting">
/// How many things are waiting on it: exchanges it owes an answer, plus messages it has not
/// collected. Counted together because both are reasons to give an agent a turn and neither is
/// visible to the other — an answer to a question this agent asked leaves no debt at all, and
/// was the case that went unnoticed until an agent sat live beside an uncollected message for
/// twenty minutes.
/// </param>
/// <param name="LastSeenUtc">When it last communicated, or null if it never has.</param>
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
/// <see cref="LastSeenUtc"/> is reported rather than filtered on, which is a change from the
/// one-shot form and a necessary one. Liveness used to mean "somebody else is running this, do
/// not start a second copy". A supervisor that keeps agents resident makes its own agents live —
/// they hold open sessions precisely because it started them — so a query that excluded live
/// agents would return every candidate exactly once and then never again. Who is live and who is
/// mine is a distinction only the supervisor can draw, so it draws it.
/// </remarks>
public sealed record InvocationCandidate(
    AgentName Agent,
    string Workspace,
    string InvokeCommand,
    long Waiting,
    DateTimeOffset? LastSeenUtc,
    long? StuckAt);

/// <summary>
/// What invocation has cost, per agent.
/// </summary>
/// <param name="Agent">Who was started.</param>
/// <param name="Invocations">How many times.</param>
/// <param name="Answered">How many left the agent owing less than it did before.</param>
/// <param name="TimedOut">How many were killed for taking too long.</param>
/// <param name="Failed">How many exited non-zero without timing out.</param>
/// <param name="TotalMs">Time spent inside those turns.</param>
/// <param name="LastUtc">When the most recent one started.</param>
/// <param name="CostUsd">What they cost in total, as the runner reported it.</param>
/// <param name="PermissionDenials">Tool calls refused for want of permission.</param>
public sealed record InvocationSummary(
    string Agent, long Invocations, long Answered, long TimedOut, long Failed,
    long TotalMs, string LastUtc, double CostUsd, long PermissionDenials)
{
    /// <summary>Gets how long an invocation takes on average.</summary>
    public long MeanMs => Invocations == 0 ? 0 : TotalMs / Invocations;

    /// <summary>
    /// Gets invocations that changed nothing: the agent ran and still owed what it owed.
    /// </summary>
    /// <remarks>
    /// The number to watch. An invocation that answers nothing still spends tokens, so a rising
    /// count here is the shape a runaway takes before it looks like one — an agent being started
    /// repeatedly for work it will not do, because it cannot, or because the question is one it
    /// has already declined.
    /// </remarks>
    public long Fruitless => Invocations - Answered - TimedOut - Failed;
}

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
    /// Four conditions, and each excludes a different mistake. The agent must actually owe
    /// something, or the turn is tokens spent on nothing. It must have opted in through
    /// <c>invoke_when_idle</c>, because running a repository somebody did not offer is spending
    /// their tokens on their machine. It must say where it works, and it must say how to be
    /// started — storage refuses consent without a command, so the last of those is a belt to
    /// that brace rather than a second rule.
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
            SELECT a.name, a.workspace, a.invoke_command,
                   count(*) AS waiting, a.last_seen_utc,
                   (SELECT CASE WHEN i.waiting_after >= i.waiting_before
                                THEN i.waiting_after END
                      FROM invocations i
                     WHERE i.agent = a.name
                     ORDER BY i.id DESC
                     LIMIT 1) AS stuck_at
              FROM waiting w
              JOIN agents a ON a.name = w.agent
             WHERE a.invoke_when_idle = 1
               AND a.status <> 'retired'
               AND a.workspace IS NOT NULL
               AND a.invoke_command IS NOT NULL
             GROUP BY a.name, a.workspace, a.invoke_command, a.last_seen_utc
             ORDER BY waiting DESC, a.name;
            """;

        using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new InvocationCandidate(
                AgentName.Create(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : ExchangeRow.ParseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return candidates;
    }

    /// <summary>
    /// Finds every agent that must not be kept running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Agents an operator has retired or withdrawn consent for.</returns>
    /// <remarks>
    /// <para>
    /// Two ways an operator withdraws an agent, and both must reach a process that is already
    /// running. Retiring it is §14's judgement that it will not come back. Removing the invoke
    /// command from its launch configuration is the consent being taken away, which the next
    /// launch of that agent's own server writes through.
    /// </para>
    /// <para>
    /// Without this, either control would apply only to agents not yet started — the shape of
    /// control §14 warns about, where the register says one thing and the deployment does
    /// another. An agent already resident would keep answering under an identity its operator
    /// had withdrawn.
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
            "SELECT name FROM agents WHERE status = 'retired' OR invoke_when_idle = 0;";

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
    /// §17 question 1's decision to leave Notify unbounded rests on measuring it, and this is the
    /// same bargain on something that costs considerably more: recording only the successes would
    /// make a deployment burning tokens on failures look idle.
    /// </para>
    /// <para>
    /// <paramref name="exitCode"/> outlived the one-shot form that named it. There is no process
    /// exit to report for a resident agent, so it now carries the turn's verdict — zero for a
    /// clean finish — and the column keeps its name because renaming it would make the rows
    /// written by the older form unreadable beside the newer ones without telling anybody
    /// anything they could not get from <c>started_utc</c>.
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

    /// <summary>Reads what invocation has cost, per agent.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One row per agent ever started, busiest first.</returns>
    public async Task<IReadOnlyList<InvocationSummary>> SummariseAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = new List<InvocationSummary>();

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT agent,
                   count(*),
                   coalesce(sum(waiting_after < waiting_before), 0),
                   coalesce(sum(timed_out), 0),
                   coalesce(sum(timed_out = 0 AND exit_code <> 0), 0),
                   coalesce(sum(elapsed_ms), 0),
                   max(started_utc),
                   coalesce(sum(cost_usd), 0.0),
                   coalesce(sum(permission_denials), 0)
              FROM invocations
             GROUP BY agent
             ORDER BY count(*) DESC, agent;
            """;

        using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new InvocationSummary(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6),
                reader.GetDouble(7), reader.GetInt64(8)));
        }

        return rows;
    }
}
