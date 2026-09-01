using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Records what the size cap refused, so the next value for it can be measured.
/// </summary>
/// <param name="connection">An open connection to the project's database.</param>
/// <remarks>
/// <para>
/// Rule 8.5 fixes a number no evidence has ever been offered for, and §17 says the same of the
/// clarify cap without anyone ever collecting it. This is the collection. It exists because the
/// alternative — changing a poisoning control on the strength of whoever complained most
/// recently — is how a control becomes a formality.
/// </para>
/// <para>
/// <b>No payload is stored, only its length.</b> Keeping the text that was refused for being too
/// long would retain exactly the material the cap exists to keep out, place it outside the
/// retention policy that governs messages, and turn instrumenting a poisoning control into a
/// poisoning vector. The measurement answers the question; the text answers nothing.
/// </para>
/// </remarks>
public sealed class SizeRefusalRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    /// <summary>Records one refusal.</summary>
    /// <param name="agent">The agent whose message was refused.</param>
    /// <param name="type">The message type.</param>
    /// <param name="characters">How long the payload was.</param>
    /// <param name="cap">The cap in force.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of rows affected.</returns>
    /// <remarks>
    /// <para>
    /// An upsert onto one row per agent and type, so the table is bounded by the registry rather
    /// than by how often an agent misbehaves — an agent looping on an oversized answer updates a
    /// row instead of filling a disk. The distribution is lost and the count, the worst case and
    /// the mean are kept, which is what choosing a number needs.
    /// </para>
    /// <para>
    /// Two processes never contend for the same row, and that falls out of the deployment rather
    /// than being arranged: the key begins with the agent, and one agent is one server process.
    /// Contention here is ordinary contention for the file, which the busy timeout covers, and a
    /// write that loses it costs one measurement and nothing else.
    /// </para>
    /// </remarks>
    public async Task<int> RecordAsync(
        AgentName agent,
        MessageType type,
        int characters,
        int cap,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO size_refusals (agent, type, refusals, worst, total, cap, last_utc)
                VALUES (@Agent, @Type, 1, @Characters, @Characters, @Cap, @LastUtc)
                ON CONFLICT(agent, type) DO UPDATE SET
                    refusals = refusals + 1,
                    worst    = max(worst, excluded.worst),
                    total    = total + excluded.total,
                    cap      = excluded.cap,
                    last_utc = excluded.last_utc;
                """,
                new
                {
                    Agent = agent.Value,
                    Type = Format(type),
                    Characters = characters,
                    Cap = cap,
                    LastUtc = ExchangeRow.FormatTimestamp(nowUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>Reads everything recorded.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per agent and message type that has ever been refused.</returns>
    public async Task<IReadOnlyList<SizeRefusal>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<Row>(
            new CommandDefinition(
                """
                SELECT agent AS Agent, type AS Type, refusals AS Refusals, worst AS Worst,
                       total AS Total, cap AS Cap, last_utc AS LastUtc
                  FROM size_refusals
                 ORDER BY worst DESC, agent, type;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new SizeRefusal(
            AgentName.Create(r.Agent),
            Parse(r.Type),
            r.Refusals,
            r.Worst,
            r.Total,
            r.Cap,
            ExchangeRow.ParseTimestamp(r.LastUtc)))];
    }

    /// <summary>Writes a message type as the schema stores it.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The type is not one of the four.</exception>
    private static string Format(MessageType type) => type switch
    {
        MessageType.Question => "question",
        MessageType.Clarify => "clarify",
        MessageType.Answer => "answer",
        MessageType.Notify => "notify",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>Reads a stored message type.</summary>
    /// <param name="value">The token.</param>
    /// <returns>The type.</returns>
    /// <exception cref="InvalidOperationException">The token is not recognised.</exception>
    private static MessageType Parse(string value) => value switch
    {
        "question" => MessageType.Question,
        "clarify" => MessageType.Clarify,
        "answer" => MessageType.Answer,
        "notify" => MessageType.Notify,
        _ => throw new InvalidOperationException($"Unrecognised message type '{value}'.")
    };

    /// <summary>Flat projection of a row in the <c>size_refusals</c> table.</summary>
    private sealed class Row
    {
        public string Agent { get; init; } = "";

        public string Type { get; init; } = "";

        public int Refusals { get; init; }

        public int Worst { get; init; }

        public long Total { get; init; }

        public int Cap { get; init; }

        public string LastUtc { get; init; } = "";
    }
}
