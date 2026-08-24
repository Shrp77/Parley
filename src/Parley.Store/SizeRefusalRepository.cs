using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// What the size cap of SPEC.md rule 8.5 refused, for one agent and one message type.
/// </summary>
/// <param name="Agent">The agent whose message was refused.</param>
/// <param name="Type">The message type it was trying to send.</param>
/// <param name="Refusals">How many times.</param>
/// <param name="Worst">The longest payload refused, in characters.</param>
/// <param name="Total">Every refused payload's length added together.</param>
/// <param name="Cap">The cap in force at the most recent refusal.</param>
/// <param name="LastUtc">When the most recent one happened.</param>
/// <remarks>
/// <see cref="Total"/> is here so an operator can see the mean rather than only the worst. A
/// single refusal at forty thousand characters and forty at four thousand and one are different
/// problems: the first is somebody trying to send a file, the second is a cap set slightly too
/// low, and only the second is an argument for changing the number.
/// </remarks>
public sealed record SizeRefusal(
    AgentName Agent,
    MessageType Type,
    int Refusals,
    int Worst,
    int Total,
    int Cap,
    DateTimeOffset LastUtc);

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
    /// An upsert onto one row per agent and type, so the table is bounded by the registry rather
    /// than by how often an agent misbehaves — an agent looping on an oversized answer updates a
    /// row instead of filling a disk. The distribution is lost and the count, the worst case and
    /// the mean are kept, which is what choosing a number needs.
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

        public int Total { get; init; }

        public int Cap { get; init; }

        public string LastUtc { get; init; } = "";
    }
}
