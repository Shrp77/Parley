using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Derives the flags of SPEC.md §6 and the recorded cycles of §7.2, for the person running the deployment.
/// </summary>
/// <param name="connection">The open, migrated database.</param>
/// <remarks>
/// <para>
/// Read-only, and computed rather than stored. Crossing a threshold changes nothing about an exchange,
/// so there is nothing to write down: an exchange that moves is simply absent from the next read, and a
/// stored flag would be one more thing that could disagree with the traffic.
/// </para>
/// <para>
/// The thresholds come from each exchange's own row, stamped at admission, so a deployment changing its
/// settings does not re-flag work already in flight.
/// </para>
/// </remarks>
public sealed class FlagRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Reads what needs the person's attention now.</summary>
    /// <param name="nowUtc">The instant to measure silence against.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The flags, oldest silence first, and the cycles in conversations still open.</returns>
    /// <remarks>
    /// <para>
    /// <b>The two stages are told apart by collection.</b> The latest message addressed to the owing
    /// participant on the exchange is what it owes a move on. If that message is uncollected, the
    /// silence is <see cref="FlagKind.NotCollected"/> and runs from when it was written. If it was
    /// collected, the silence is <see cref="FlagKind.NoProgress"/> and runs from the later of its
    /// collection and the owing participant's last message anywhere in the conversation — because
    /// progress on a line of work often shows up as a Request to somebody else rather than as a message on
    /// this exchange.
    /// </para>
    /// <para>
    /// An exchange owed by a retired agent is flagged whatever its timing (§14): somebody asked for that
    /// work and nobody is going to do it.
    /// </para>
    /// </remarks>
    public async Task<FlagReport> ReadAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        using var snapshot = _connection.BeginTransaction(deferred: true);

        var rows = await _connection.QueryAsync<OpenRow>(
            new CommandDefinition(
                """
                SELECT e.id                  AS Id,
                       e.conversation_id     AS ConversationId,
                       e.requester           AS Requester,
                       e.receiver            AS Receiver,
                       e.owed_by             AS OwedBy,
                       e.opened_utc          AS OpenedUtc,
                       e.uncollected_flag_ms AS UncollectedMs,
                       e.stalled_flag_ms     AS StalledMs,
                       a.status              AS OwedByStatus,
                       (SELECT m.created_utc FROM messages m
                         WHERE m.exchange_id = e.id AND m.to_agent = e.owed_by
                         ORDER BY m.id DESC LIMIT 1) AS OwedMessageCreated,
                       (SELECT m.delivered_utc FROM messages m
                         WHERE m.exchange_id = e.id AND m.to_agent = e.owed_by
                         ORDER BY m.id DESC LIMIT 1) AS OwedMessageDelivered,
                       (SELECT max(m.created_utc) FROM messages m
                         WHERE m.conversation_id = e.conversation_id
                           AND m.from_agent = e.owed_by) AS LastFromOwed,
                       (SELECT i.started_utc FROM invocations i
                         WHERE i.agent = e.owed_by ORDER BY i.id DESC LIMIT 1) AS LastTurnStarted,
                       coalesce((SELECT i.elapsed_ms FROM invocations i
                         WHERE i.agent = e.owed_by ORDER BY i.id DESC LIMIT 1), 0) AS LastTurnElapsedMs,
                       coalesce((SELECT i.permission_denials FROM invocations i
                         WHERE i.agent = e.owed_by ORDER BY i.id DESC LIMIT 1), 0) AS LastTurnDenials
                  FROM exchanges e
                  JOIN agents a ON a.name = e.owed_by
                 WHERE e.state = 'OPEN'
                 ORDER BY e.id;
                """,
                transaction: snapshot,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var flags = new List<Flag>();

        foreach (var row in rows)
        {
            var owedBy = AgentName.Create(row.OwedBy);
            var waiting = owedBy.Value == row.Receiver
                ? AgentName.Create(row.Requester)
                : AgentName.Create(row.Receiver);

            var written = ExchangeRow.ParseTimestamp(row.OwedMessageCreated ?? row.OpenedUtc);

            if (row.OwedByStatus == "retired")
            {
                flags.Add(new Flag(FlagKind.Retired, row.Id, row.ConversationId, owedBy, waiting, written));
                continue;
            }

            if (row.OwedMessageDelivered is null)
            {
                if (nowUtc - written >= TimeSpan.FromMilliseconds(row.UncollectedMs))
                {
                    flags.Add(new Flag(
                        FlagKind.NotCollected, row.Id, row.ConversationId, owedBy, waiting, written));
                }

                continue;
            }

            var progress = ExchangeRow.ParseTimestamp(row.OwedMessageDelivered);

            if (row.LastFromOwed is { } last && ExchangeRow.ParseTimestamp(last) is var said && said > progress)
            {
                progress = said;
            }

            // **Stopped on a refused permission: at once, not after the threshold** (SPEC.md §6, specs/024). The owing
            // agent's most recent turn ended after it took this work and after it last said anything here, and refused
            // at least one tool call for want of permission. A resident agent runs with nobody at the keyboard, so such a
            // call is refused rather than paused, and nothing the agent can do gets it further — there is nothing to be
            // patient with. Read off the most recent turn, so a later clean turn clears it (research R6).
            if (row.LastTurnDenials > 0
                && row.LastTurnStarted is { } began
                && ExchangeRow.ParseTimestamp(began).AddMilliseconds(row.LastTurnElapsedMs) is var ended
                && ended > progress)
            {
                flags.Add(new Flag(FlagKind.PermissionRefused, row.Id, row.ConversationId, owedBy, waiting, ended));
                continue;
            }

            if (nowUtc - progress >= TimeSpan.FromMilliseconds(row.StalledMs))
            {
                flags.Add(new Flag(FlagKind.NoProgress, row.Id, row.ConversationId, owedBy, waiting, progress));
            }
        }

        // A poll that nothing but a vote is holding open. Dated from when it became so: the latest of the poll,
        // the last agreement and the last exchange to close.
        var silent = await _connection.QueryAsync<SilentRow>(
            new CommandDefinition(
                $"""
                SELECT p.conversation_id AS ConversationId,
                       p.agent AS Agent,
                       coalesce((SELECT v.agent FROM conversation_participants v
                                  WHERE v.conversation_id = c.id AND v.vote = 'done'
                                  ORDER BY v.voted_utc, v.agent LIMIT 1), p.agent) AS Finished,
                       max(p.polled_utc,
                           coalesce((SELECT max(v.voted_utc) FROM conversation_participants v
                                      WHERE v.conversation_id = c.id AND v.vote = 'done'), p.polled_utc),
                           coalesce((SELECT max(x.closed_utc) FROM exchanges x
                                      WHERE x.conversation_id = c.id), p.polled_utc)) AS SinceUtc
                {ConversationRepository.SilentVoters}
                 ORDER BY p.conversation_id, p.agent;
                """,
                transaction: snapshot,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        flags.AddRange(silent.Select(s => new Flag(
            FlagKind.NotVoted, 0, s.ConversationId, AgentName.Create(s.Agent), AgentName.Create(s.Finished),
            ExchangeRow.ParseTimestamp(s.SinceUtc))));

        var cycles = await _connection.QueryAsync<CycleRow>(
            new CommandDefinition(
                """
                SELECT c.exchange_id AS ExchangeId, c.conversation_id AS ConversationId,
                       c.path AS Path, c.recorded_utc AS RecordedUtc
                  FROM conversation_cycles c
                 WHERE EXISTS (SELECT 1 FROM exchanges x
                                WHERE x.conversation_id = c.conversation_id AND x.state = 'OPEN')
                 ORDER BY c.recorded_utc, c.exchange_id;
                """,
                transaction: snapshot,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new FlagReport(
            [.. flags.OrderBy(f => f.SinceUtc).ThenBy(f => f.ExchangeId)],
            [.. cycles.Select(c => new RecordedCycle(
                c.ExchangeId,
                c.ConversationId,
                [.. c.Path.Split(ExchangeRepository.CyclePathSeparator)
                    .Where(name => AgentName.TryCreate(name, out _))
                    .Select(AgentName.Create)],
                ExchangeRow.ParseTimestamp(c.RecordedUtc)))]);
    }

    private sealed class OpenRow
    {
        public long Id { get; init; }

        public string ConversationId { get; init; } = "";

        public string Requester { get; init; } = "";

        public string Receiver { get; init; } = "";

        public string OwedBy { get; init; } = "";

        public string OpenedUtc { get; init; } = "";

        public long UncollectedMs { get; init; }

        public long StalledMs { get; init; }

        public string OwedByStatus { get; init; } = "";

        public string? OwedMessageCreated { get; init; }

        public string? OwedMessageDelivered { get; init; }

        public string? LastFromOwed { get; init; }

        public string? LastTurnStarted { get; init; }

        public long LastTurnElapsedMs { get; init; }

        public long LastTurnDenials { get; init; }
    }

    private sealed class SilentRow
    {
        public string ConversationId { get; init; } = "";

        public string Agent { get; init; } = "";

        public string Finished { get; init; } = "";

        public string SinceUtc { get; init; } = "";
    }

    private sealed class CycleRow
    {
        public long ExchangeId { get; init; }

        public string ConversationId { get; init; } = "";

        public string Path { get; init; } = "";

        public string RecordedUtc { get; init; } = "";
    }
}
