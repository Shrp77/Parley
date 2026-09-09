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
    /// <remarks>
    /// Kept beside <see cref="ListAsync"/> rather than folded into it. Narrowing by identifier is a
    /// genuine single-row read, and expressing it as a one-element page would make every caller
    /// unwrap a list to ask a question that has one answer.
    /// </remarks>
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

        return row is null ? null : new InstructionResult(Read(row.Outcome), row.Reply, id);
    }

    /// <summary>Reads back a bounded page of instructions, newest first.</summary>
    /// <param name="agent">Narrow to one agent, or null for every agent.</param>
    /// <param name="id">Narrow to exactly one instruction, or null for all that match.</param>
    /// <param name="limit">How many to return. The caller is expected to have bounded this.</param>
    /// <param name="beforeId">Resume before this identifier, or null to start at the newest.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The instructions, newest first.</returns>
    /// <remarks>
    /// <para>
    /// <b>The operation SPEC.md §10.2 promised and did not have.</b> The section says the reply "is
    /// kept beside the instruction rather than in the message log", and it was — in this table, with
    /// nothing able to ask for it. The only route to an agent's answer was the return value of the
    /// call that issued the instruction, so anything that interrupted that call lost the answer for
    /// ever while the broker still held it.
    /// </para>
    /// <para>
    /// The cursor is an identifier rather than an instant, because these are monotonic and cannot
    /// tie. The conversation listing needs both for exactly that reason; this does not, and
    /// borrowing its shape would be carrying a tie-break for ties that cannot happen.
    /// </para>
    /// <para>
    /// Refuses nothing about the agent name. A name that cannot be parsed is a caller error and
    /// belongs at the wire edge, where the caller can be told; here it would have to be expressed
    /// as an empty result, which is the shape §10.2 warns about — a narrow question answered with a
    /// wide result, indistinguishable from an honest empty one.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<InstructionRecord>> ListAsync(
        AgentName? agent = null,
        long? id = null,
        int limit = 50,
        long? beforeId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var rows = await _connection.QueryAsync<InstructionListRow>(
            new CommandDefinition(
                """
                SELECT id           AS Id,
                       agent        AS Agent,
                       text         AS Text,
                       outcome      AS Outcome,
                       reply        AS Reply,
                       issued_utc   AS IssuedUtc,
                       settled_utc  AS SettledUtc
                  FROM instructions
                 WHERE (@Agent IS NULL OR agent = @Agent)
                   AND (@Id IS NULL OR id = @Id)
                   AND (@BeforeId IS NULL OR id < @BeforeId)
                 ORDER BY id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    Agent = agent?.Value,
                    Id = id,
                    BeforeId = beforeId,
                    Limit = limit
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(row => new InstructionRecord(
            row.Id,
            AgentName.Create(row.Agent),
            row.Text,
            Read(row.Outcome),
            row.Reply,
            ExchangeRow.ParseTimestamp(row.IssuedUtc),
            row.SettledUtc is null ? null : ExchangeRow.ParseTimestamp(row.SettledUtc)))];
    }

    /// <summary>Settles every instruction still running that was issued before an instant.</summary>
    /// <param name="issuedBeforeUtc">The instant; anything older is presumed dead.</param>
    /// <param name="nowUtc">When the sweep ran.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many were settled.</returns>
    /// <remarks>
    /// <para>
    /// <b>The backstop for a machine that vanished without disconnecting.</b> A node leaving settles
    /// its own agents' instructions precisely, which is the primary mechanism; this exists because a
    /// node can stay connected while the agent process behind it dies, and nothing else would ever
    /// close those.
    /// </para>
    /// <para>
    /// <b>Aged from when the instruction was issued, which is the only instant a running one has.</b>
    /// The schema pairs the settled instant with a settled outcome, so a pending row's is null by
    /// construction — a sweep measuring from settlement would match nothing at all, and would look
    /// like a working sweep while being incapable of touching the only rows it exists for.
    /// </para>
    /// <para>
    /// Cannot disturb a settled instruction, and not by being careful: the same
    /// <c>outcome = 'pending'</c> clause that makes settlement first-come is what bounds this. The
    /// guarantee is in the statement rather than in the caller.
    /// </para>
    /// </remarks>
    public async Task<int> SweepAsync(
        DateTimeOffset issuedBeforeUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE instructions
                   SET outcome = 'failed', settled_utc = @Now
                 WHERE outcome = 'pending'
                   AND issued_utc < @Before;
                """,
                new
                {
                    Now = ExchangeRow.FormatTimestamp(nowUtc),
                    Before = ExchangeRow.FormatTimestamp(issuedBeforeUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>Reads an outcome the store holds.</summary>
    /// <param name="stored">The column's value.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="InvalidOperationException">The value is one this build does not know.</exception>
    private static InstructionOutcome Read(string stored) => stored switch
    {
        "pending" => InstructionOutcome.Pending,
        "taken" => InstructionOutcome.Taken,
        "declined" => InstructionOutcome.Declined,
        "undeliverable" => InstructionOutcome.Undeliverable,
        "failed" => InstructionOutcome.Failed,
        _ => throw new InvalidOperationException(
            $"The store holds an outcome this build does not know: {stored}.")
    };

    /// <summary>One instruction, as the store holds it.</summary>
    private sealed class InstructionRow
    {
        public string Outcome { get; init; } = string.Empty;

        public string? Reply { get; init; }
    }

    /// <summary>One instruction read back whole.</summary>
    private sealed class InstructionListRow
    {
        public long Id { get; init; }

        public string Agent { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;

        public string Outcome { get; init; } = string.Empty;

        public string? Reply { get; init; }

        public string IssuedUtc { get; init; } = string.Empty;

        public string? SettledUtc { get; init; }
    }
}
