using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// The instructions an operator has given, and what became of each.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §10.2's <c>instruct</c>. These are the only rows in this store a person wrote, and
/// they are deliberately not messages: <c>messages.from_agent</c> is a foreign key to
/// <c>agents(name)</c> and an operator is not an agent. Relaxing that key to admit one sender
/// which is not registered would admit every other unregistered sender in the same edit.
/// </para>
/// <para>
/// An instruction is written before it is delivered, not after. The row is the record that
/// somebody asked, which is worth having even when the asking failed — an operator wanting to
/// know what they told an agent last Tuesday is asking about the instruction, not about whether
/// the machine happened to be on.
/// </para>
/// </remarks>
/// <param name="connection">An open connection to the project's database.</param>
public sealed class InstructionRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection
        ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Records an instruction before anybody tries to deliver it.</summary>
    /// <param name="agent">Who it is for.</param>
    /// <param name="text">What the operator said.</param>
    /// <param name="nowUtc">When they said it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The instruction's identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    public async Task<long> RecordAsync(
        AgentName agent,
        string text,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        return await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO instructions (agent, text, outcome, issued_utc)
                VALUES (@Agent, @Text, 'pending', @Now)
                RETURNING id;
                """,
                new
                {
                    Agent = agent.Value,
                    Text = text,
                    Now = ExchangeRow.FormatTimestamp(nowUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Records what became of an instruction.</summary>
    /// <param name="id">Which instruction.</param>
    /// <param name="outcome">What became of it.</param>
    /// <param name="reply">What the agent said, if it said anything.</param>
    /// <param name="nowUtc">When it settled.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether a pending instruction was settled.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is <c>Pending</c>.</exception>
    /// <remarks>
    /// Settling to <c>pending</c> is refused here as well as in the schema. The column's CHECK
    /// pairs the outcome with the settled timestamp, so the write would fail anyway — but it
    /// would fail as a constraint violation from three layers down, and a caller reading that
    /// would learn about SQLite rather than about what it did wrong.
    /// </remarks>
    public async Task<bool> SettleAsync(
        long id,
        InstructionOutcome outcome,
        string? reply,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (outcome == InstructionOutcome.Pending)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome, "An instruction cannot settle as pending.");
        }

        var rows = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE instructions
                   SET outcome = @Outcome, reply = @Reply, settled_utc = @Now
                 WHERE id = @Id AND outcome = 'pending';
                """,
                new
                {
                    Id = id,
                    Outcome = outcome.ToString().ToLowerInvariant(),
                    Reply = reply,
                    Now = ExchangeRow.FormatTimestamp(nowUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>What one instruction became.</summary>
    /// <param name="id">Which instruction.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The outcome and reply, or <see langword="null"/> if there is no such instruction.</returns>
    public async Task<InstructionResult?> GetAsync(
        long id, CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<InstructionRow>(
            new CommandDefinition(
                """
                SELECT outcome AS Outcome, reply AS Reply
                  FROM instructions
                 WHERE id = @Id;
                """,
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new InstructionResult(
                row.Outcome switch
                {
                    "pending" => InstructionOutcome.Pending,
                    "taken" => InstructionOutcome.Taken,
                    "declined" => InstructionOutcome.Declined,
                    "undeliverable" => InstructionOutcome.Undeliverable,
                    "failed" => InstructionOutcome.Failed,
                    _ => throw new InvalidOperationException(
                        $"The store holds an outcome this build does not know: {row.Outcome}.")
                },
                row.Reply);
    }

    /// <summary>One instruction, as the store holds it.</summary>
    private sealed class InstructionRow
    {
        public string Outcome { get; init; } = string.Empty;

        public string? Reply { get; init; }
    }
}
