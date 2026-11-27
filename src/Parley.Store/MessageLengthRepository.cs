using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>Reads the record of how long messages were (SPEC.md §10.2, <c>message_lengths</c>).</summary>
/// <param name="connection">An open connection.</param>
/// <remarks>
/// Only reads. A trigger writes the record, so that no route that stores a message can forget to. Worked out on each
/// read rather than kept as a summary: a person reads this now and then, and a summary would be one more thing to keep
/// right.
/// </remarks>
public sealed class MessageLengthRepository(SqliteConnection connection)
{
    private const string Types = "type";

    private const string Positions =
        """
        CASE WHEN position <= 5 THEN '1 to 5'
             WHEN position <= 20 THEN '6 to 20'
             WHEN position <= 100 THEN '21 to 100'
             ELSE '101 and later' END
        """;

    private static readonly string[] PositionOrder = ["1 to 5", "6 to 20", "21 to 100", "101 and later"];

    private readonly SqliteConnection _connection = connection;

    /// <summary>Reads the report.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The report. With nothing recorded, every count is zero.</returns>
    public async Task<MessageLengthReport> ReadAsync(CancellationToken cancellationToken = default)
    {
        var overall = await GroupsAsync("'all'", cancellationToken).ConfigureAwait(false);
        var types = await GroupsAsync(Types, cancellationToken).ConfigureAwait(false);
        var positions = await GroupsAsync(Positions, cancellationToken).ConfigureAwait(false);

        var agents = await _connection.QueryAsync<AgentRow>(
            new CommandDefinition(
                """
                SELECT agent AS Agent,
                       sum(sent) AS Sent,
                       sum(1 - sent) AS Received,
                       max(characters) AS Longest,
                       sum(characters > @Soft) AS OverSoftLimit
                  FROM (SELECT from_agent AS agent, 1 AS sent, characters FROM message_lengths
                        UNION ALL
                        SELECT to_agent, 0, characters FROM message_lengths)
                 GROUP BY agent
                 ORDER BY Longest DESC, agent;
                """,
                new { Soft = MessageLimits.SoftPayloadCharacters },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new MessageLengthReport(
            overall.SingleOrDefault() ?? new MessageLengthGroup("all", 0, 0, 0, 0),
            types,
            [.. positions.OrderBy(group => Array.IndexOf(PositionOrder, group.Name))],
            [.. agents.Select(a => new AgentMessageLengths(
                AgentName.Create(a.Agent), a.Sent, a.Received, a.Longest, a.OverSoftLimit))]);
    }

    /// <summary>Reads the groups an expression sorts the records into.</summary>
    /// <param name="group">A constant of this class. Never a caller's text.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per group that has any records.</returns>
    private async Task<IReadOnlyList<MessageLengthGroup>> GroupsAsync(string group, CancellationToken cancellationToken)
    {
        var rows = await _connection.QueryAsync<GroupRow>(
            new CommandDefinition(
                $"""
                WITH ranked AS (
                  SELECT {group} AS name, characters,
                         row_number() OVER (PARTITION BY {group} ORDER BY characters) AS n,
                         count(*) OVER (PARTITION BY {group}) AS total
                    FROM message_lengths)
                SELECT name AS Name,
                       max(total) AS Messages,
                       max(CASE WHEN n = (total + 1) / 2 THEN characters END) AS Median,
                       max(characters) AS Longest,
                       sum(characters > @Soft) AS OverSoftLimit
                  FROM ranked
                 GROUP BY name
                 ORDER BY name;
                """,
                new { Soft = MessageLimits.SoftPayloadCharacters },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new MessageLengthGroup(r.Name, r.Messages, r.Median, r.Longest, r.OverSoftLimit))];
    }

    /// <summary>Flat projection of one group.</summary>
    private sealed class GroupRow
    {
        public string Name { get; init; } = "";

        public long Messages { get; init; }

        public int Median { get; init; }

        public int Longest { get; init; }

        public long OverSoftLimit { get; init; }
    }

    /// <summary>Flat projection of one agent.</summary>
    private sealed class AgentRow
    {
        public string Agent { get; init; } = "";

        public long Sent { get; init; }

        public long Received { get; init; }

        public int Longest { get; init; }

        public long OverSoftLimit { get; init; }
    }
}
