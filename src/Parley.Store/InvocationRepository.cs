using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// An agent that owes an answer and has nobody running it.
/// </summary>
/// <param name="Agent">Who to start.</param>
/// <param name="Workspace">The directory it works in.</param>
/// <param name="Owed">How many exchanges are waiting on it.</param>
public sealed record InvocationCandidate(AgentName Agent, string Workspace, long Owed);

/// <summary>
/// What invocation has cost, per agent.
/// </summary>
/// <param name="Agent">Who was started.</param>
/// <param name="Invocations">How many times.</param>
/// <param name="Answered">How many left the agent owing less than it did before.</param>
/// <param name="TimedOut">How many were killed for taking too long.</param>
/// <param name="Failed">How many exited non-zero without timing out.</param>
/// <param name="TotalMs">Time spent inside those processes.</param>
/// <param name="LastUtc">When the most recent one started.</param>
public sealed record InvocationSummary(
    string Agent, long Invocations, long Answered, long TimedOut, long Failed,
    long TotalMs, string LastUtc)
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
    /// Finds agents that owe an answer, may be started, and are not already running.
    /// </summary>
    /// <param name="staleBefore">The liveness cutoff: an agent last seen before this is idle.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Who to start, busiest first.</returns>
    /// <remarks>
    /// <para>
    /// Three conditions, and each excludes a different mistake. The agent must actually owe
    /// something, or the invocation is a process started to do nothing. It must have opted in
    /// through <c>invoke_when_idle</c>, because starting a repository somebody did not offer is
    /// spending their tokens on their machine. And it must be idle.
    /// </para>
    /// <para>
    /// Idle is the interesting one and it reuses §14's lease rather than inventing a rule. An
    /// agent with a live session is Pull: it will drain its own inbox when its person takes a
    /// turn, and starting a second copy would have two processes answering as one identity —
    /// which the exchange's turn check would refuse, after both had spent tokens getting there.
    /// The lease already distinguishes "recently heard from" from "recorded as live", and §14 is
    /// explicit that only the first is trustworthy.
    /// </para>
    /// <para>
    /// A retired agent is excluded by the status filter for the same reason §14 refuses to route
    /// to one: retirement is a person's judgement that the agent will not come back, and a
    /// process started against it would overrule that.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<InvocationCandidate>> FindOwedAsync(
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<InvocationCandidate>();

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.name, a.workspace, count(*) AS owed
              FROM exchanges e
              JOIN agents a ON a.name = e.owed_by
             WHERE e.state = 'OPEN'
               AND a.invoke_when_idle = 1
               AND a.status <> 'retired'
               AND a.workspace IS NOT NULL
               AND (a.last_seen_utc IS NULL OR a.last_seen_utc < @StaleBefore)
             GROUP BY a.name, a.workspace
             ORDER BY owed DESC, a.name;
            """;

        command.Parameters.AddWithValue(
            "@StaleBefore", ExchangeRow.FormatTimestamp(staleBefore));

        using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new InvocationCandidate(
                AgentName.Create(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2)));
        }

        return candidates;
    }

    /// <summary>Counts what an agent currently owes.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Open exchanges waiting on it.</returns>
    /// <remarks>
    /// Read before and after an invocation, so the record can say whether the process changed
    /// anything. An agent that ran and still owes exactly what it owed is the one worth noticing.
    /// </remarks>
    public async Task<long> CountOwedAsync(
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM exchanges WHERE state = 'OPEN' AND owed_by = @Agent;";
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
    /// <param name="exitCode">Its exit code.</param>
    /// <param name="timedOut">Whether it was killed for taking too long.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Rows written.</returns>
    /// <remarks>
    /// Every invocation is recorded, including the ones that failed and the ones that achieved
    /// nothing. §17 question 1's decision to leave Notify unbounded rests on measuring it, and
    /// this is the same bargain on something that costs considerably more: recording only the
    /// successes would make a deployment burning tokens on failures look idle.
    /// </remarks>
    public async Task<int> RecordAsync(
        AgentName agent,
        long owedBefore,
        long owedAfter,
        DateTimeOffset startedUtc,
        TimeSpan elapsed,
        int exitCode,
        bool timedOut,
        CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO invocations
                (agent, owed_before, owed_after, started_utc, elapsed_ms, exit_code, timed_out)
            VALUES (@Agent, @Before, @After, @Started, @Elapsed, @Exit, @TimedOut);
            """;

        command.Parameters.AddWithValue("@Agent", agent.Value);
        command.Parameters.AddWithValue("@Before", owedBefore);
        command.Parameters.AddWithValue("@After", owedAfter);
        command.Parameters.AddWithValue("@Started", ExchangeRow.FormatTimestamp(startedUtc));
        command.Parameters.AddWithValue("@Elapsed", (long)elapsed.TotalMilliseconds);
        command.Parameters.AddWithValue("@Exit", exitCode);
        command.Parameters.AddWithValue("@TimedOut", timedOut ? 1 : 0);

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
                   coalesce(sum(owed_after < owed_before), 0),
                   coalesce(sum(timed_out), 0),
                   coalesce(sum(timed_out = 0 AND exit_code <> 0), 0),
                   coalesce(sum(elapsed_ms), 0),
                   max(started_utc)
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
                reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6)));
        }

        return rows;
    }
}
