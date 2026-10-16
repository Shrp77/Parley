using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// The facilitator's questions and the project's Auto Accept switch (SPEC.md §10.2, §9.2).
/// </summary>
/// <param name="connection">The store.</param>
/// <remarks>
/// <para>
/// <b>What is enforced here is enforced again below.</b> Storage refuses a second live question for an agent, a question
/// that is not the facilitator's own Request, an acceptance with no line and a settled question reopened. Nothing in
/// this type relies on that, and nothing in it tries to be the only guard: a check here is for a sentence, and the
/// schema is for the caller written next year.
/// </para>
/// <para>
/// <b>Plain SQL, no domain logic.</b> Which question is due, what an answer says and what becomes of it are the
/// facilitator's decisions (<c>Parley.Broker.Facilitator</c>); this reads and writes the rows those decisions produce.
/// </para>
/// </remarks>
public sealed class OwnershipQuestionRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>The agents that should be asked now: among those attached, every one never asked and with no line.</summary>
    /// <param name="attached">The agents attached right now, by name.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The agents, in name order.</returns>
    /// <remarks>
    /// <para>
    /// <b>Attached now, because the facilitator never starts an agent to ask it</b>. One that is not running
    /// is asked the next time it attaches, and this is what notices: a registration, a reattachment and a broker
    /// starting are each "attached now and never asked", so one rule covers all three.
    /// </para>
    /// <para>
    /// <b>Never asked, not merely nothing live.</b> A question in any state stops this, a discarded one included — a
    /// person discarding an answer is a decision, and asking again on the next sweep would overrule it with nobody
    /// having chosen to spend the agent's turn. Only <c>ask_again</c> asks again.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentName>> DueAsync(
        IReadOnlyCollection<string> attached,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attached);

        if (attached.Count == 0)
        {
            return [];
        }

        var names = await _connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT a.name
                  FROM agents AS a
                 WHERE a.name IN @Attached
                   AND a.status = 'active'
                   AND a.adapter NOT IN (@Facilitator, @Seat)
                   AND NOT EXISTS (SELECT 1 FROM agent_ownership_accepted o WHERE o.agent = a.name)
                   AND NOT EXISTS (SELECT 1 FROM ownership_questions q WHERE q.agent = a.name)
                 ORDER BY a.name;
                """,
                new { Attached = attached.ToArray(), Facilitator = Facilitation.Adapter, Seat = PersonSeat.Adapter },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. names.Select(AgentName.Create)];
    }

    /// <summary>Whether an identifier is already a conversation's, or was one before it was destroyed.</summary>
    /// <param name="conversationId">The identifier.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether it is taken.</returns>
    /// <remarks>
    /// A destroyed identifier stays taken (§14), so a new question never appears under the name of one somebody
    /// already holds an export of.
    /// </remarks>
    public async Task<bool> ConversationTakenAsync(
        string conversationId,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT (SELECT count(*) FROM conversations WHERE id = @Id)
                     + (SELECT count(*) FROM destroyed_conversations WHERE id = @Id);
                """,
                new { Id = conversationId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>Records a question just asked.</summary>
    /// <param name="agent">Who was asked.</param>
    /// <param name="conversationId">Its conversation.</param>
    /// <param name="exchangeId">Its Request's exchange.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="transaction">The transaction that opened the exchange.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when it is written.</returns>
    public async Task InsertAsync(
        AgentName agent,
        string conversationId,
        long exchangeId,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO ownership_questions (agent, conversation_id, exchange_id, state, asked_utc)
                VALUES (@Agent, @Conversation, @Exchange, 'asked', @When);
                """,
                new
                {
                    Agent = agent.Value,
                    Conversation = conversationId,
                    Exchange = exchangeId,
                    When = ExchangeRow.FormatTimestamp(nowUtc)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Every question still waiting on the agent or on a person's reply, beside its exchange and the latest thing the
    /// agent said in it.
    /// </summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One row per open question.</returns>
    public async Task<IReadOnlyList<OpenQuestion>> OpenAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<OpenQuestionRow>(
            new CommandDefinition(
                """
                SELECT q.id               AS Id,
                       q.agent            AS Agent,
                       q.conversation_id  AS Conversation,
                       q.exchange_id      AS Exchange,
                       q.state            AS State,
                       e.state            AS ExchangeState,
                       e.owed_by          AS OwedBy,
                       -- The agent's own words only. A person's close writes a `closed` Result under the receiver's
                       -- name, attributed to the broker (§5), and reading that as the agent's answer would make a
                       -- sentence nobody wrote its ownership line.
                       (SELECT m.type FROM messages m
                         WHERE m.exchange_id = q.exchange_id AND m.from_agent = q.agent
                           AND m.responder_model NOT LIKE 'parley:%'
                         ORDER BY m.id DESC LIMIT 1) AS LastType,
                       (SELECT m.payload_json FROM messages m
                         WHERE m.exchange_id = q.exchange_id AND m.from_agent = q.agent
                           AND m.responder_model NOT LIKE 'parley:%'
                         ORDER BY m.id DESC LIMIT 1) AS LastPayload
                  FROM ownership_questions AS q
                  JOIN exchanges AS e ON e.id = q.exchange_id
                 WHERE q.state IN ('asked', 'asked_back')
                 ORDER BY q.id;
                """,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new OpenQuestion(
            r.Id,
            AgentName.Create(r.Agent),
            r.Conversation,
            r.Exchange,
            Parse(r.State),
            string.Equals(r.ExchangeState, "CLOSED", StringComparison.Ordinal),
            AgentName.Create(r.OwedBy),
            r.LastType is null ? null : MessageIdentity.ParseType(r.LastType),
            r.LastPayload))];
    }

    /// <summary>Records that Parley has read what an agent sent it on a question's exchange.</summary>
    /// <param name="exchangeId">The question's exchange.</param>
    /// <param name="facilitator">The facilitator's name.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when it is written.</returns>
    /// <remarks>
    /// <b>Parley never drains an inbox</b>, because nothing attaches as it: it reads its questions' exchanges directly.
    /// Without this every answer sent to it would say "not collected yet" in every transcript for ever, which reads as
    /// a fault, and retention never removes a message nobody collected — so they would also never go.
    /// </remarks>
    public async Task CollectedAsync(
        long exchangeId,
        AgentName facilitator,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE messages
                   SET delivered_utc = @Now
                 WHERE exchange_id = @Exchange AND to_agent = @Facilitator AND delivered_utc IS NULL;
                """,
                new { Exchange = exchangeId, Facilitator = facilitator.Value, Now = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>Moves a question to a state that carries nothing but itself.</summary>
    /// <param name="id">The question.</param>
    /// <param name="state"><see cref="QuestionState.Asked"/> or <see cref="QuestionState.AskedBack"/>.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it moved.</returns>
    public async Task<bool> MoveAsync(
        long id,
        QuestionState state,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (state is not (QuestionState.Asked or QuestionState.AskedBack))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Only asked and asked back carry nothing else.");
        }

        return await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE ownership_questions SET state = @State WHERE id = @Id AND state IN ('asked', 'asked_back');",
                new { Id = id, State = Format(state) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    /// <summary>Records the agent's answer, before anything is decided about it.</summary>
    /// <param name="id">The question.</param>
    /// <param name="answer">What the agent wrote.</param>
    /// <param name="flattened">The same words on one line.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was recorded.</returns>
    public async Task<bool> AnsweredAsync(
        long id,
        string answer,
        string flattened,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE ownership_questions
                   SET state = 'answered', answer = @Answer, flattened = @Flattened
                 WHERE id = @Id AND state IN ('asked', 'asked_back');
                """,
                new { Id = id, Answer = answer, Flattened = flattened },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>Holds an answer for a person, saying why.</summary>
    /// <param name="id">The question.</param>
    /// <param name="because">Why it was not accepted automatically.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it moved.</returns>
    public async Task<bool> WaitAsync(
        long id,
        string because,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE ownership_questions
                   SET state = 'waiting', waiting_because = @Because
                 WHERE id = @Id AND state = 'answered';
                """,
                new { Id = id, Because = Bounded(because) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>Settles an agent's live question as accepted, once its line is recorded.</summary>
    /// <param name="agent">Whose question.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="transaction">The transaction that recorded the line.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The conversation it was asked in, or <see langword="null"/> when nothing was live to settle.</returns>
    /// <remarks>
    /// Keyed by the agent rather than the question, because a person accepting from the review records a line for an
    /// agent and whatever it was asked is what that acceptance settles.
    /// </remarks>
    public async Task<string?> AcceptAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await SettleAsync(agent, "accepted", nowUtc, transaction, cancellationToken).ConfigureAwait(false);

    /// <summary>Whether one question is still answered and undecided.</summary>
    /// <param name="id">The question.</param>
    /// <param name="transaction">The transaction the decision is made in.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    /// Asked inside the transaction that records the line, before it does: storage accepts a question only once its line
    /// is recorded, so the check cannot be the settling itself.
    /// </remarks>
    public async Task<bool> StillAnsweredAsync(
        long id,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM ownership_questions WHERE id = @Id AND state = 'answered';",
                new { Id = id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>Settles one question as accepted, only while it is still the answered one a pass read.</summary>
    /// <param name="id">The question.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="transaction">The transaction that will record the line.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was still answered and is now accepted.</returns>
    /// <remarks>
    /// <b>Keyed by the question, for Auto Accept alone.</b> A pass reads an answer and decides it later; a person who
    /// recorded a line meanwhile has settled that question, and settling by agent would overwrite their line with the
    /// agent's. A question no longer answered is the person's decision and is left alone.
    /// </remarks>
    public async Task<bool> AcceptAnsweredAsync(
        long id,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE ownership_questions
                   SET state = 'accepted', settled_utc = @When, waiting_because = NULL
                 WHERE id = @Id AND state = 'answered';
                """,
                new { Id = id, When = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>Settles an agent's waiting answer, or a question nobody will answer, as discarded.</summary>
    /// <param name="agent">Whose question.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The conversation it was asked in, or <see langword="null"/> when nothing was live to settle.</returns>
    public async Task<string?> DiscardAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await SettleAsync(agent, "discarded", nowUtc, transaction, cancellationToken).ConfigureAwait(false);

    /// <summary>The latest question for every agent ever asked.</summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One per agent, in name order.</returns>
    public async Task<IReadOnlyList<OwnershipQuestion>> LatestAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<QuestionRow>(
            new CommandDefinition(
                """
                SELECT q.agent AS Agent, q.conversation_id AS Conversation, q.exchange_id AS Exchange,
                       q.state AS State, q.answer AS Answer, q.flattened AS Flattened,
                       q.waiting_because AS WaitingBecause, q.asked_utc AS AskedUtc, q.settled_utc AS SettledUtc
                  FROM ownership_questions AS q
                 WHERE q.id = (SELECT max(id) FROM ownership_questions WHERE agent = q.agent)
                 ORDER BY q.agent;
                """,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>The live question for one agent, if it has one.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The question, or <see langword="null"/>.</returns>
    public async Task<OwnershipQuestion?> LiveAsync(
        AgentName agent,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<QuestionRow>(
            new CommandDefinition(
                """
                SELECT agent AS Agent, conversation_id AS Conversation, exchange_id AS Exchange,
                       state AS State, answer AS Answer, flattened AS Flattened,
                       waiting_because AS WaitingBecause, asked_utc AS AskedUtc, settled_utc AS SettledUtc
                  FROM ownership_questions
                 WHERE agent = @Agent AND state IN ('asked', 'asked_back', 'answered', 'waiting');
                """,
                new { Agent = agent.Value },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>
    /// Agents whose first line was just accepted automatically and whose node has not yet been asked to refresh them.
    /// </summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The question and its agent, one per agent.</returns>
    /// <remarks>
    /// <para>
    /// <b>First line only</b>: an agent that has ever had a refresh sent has one row saying so and is never listed
    /// again, whatever happens to its line later (SPEC.md §11.1). Every later change waits for a natural restart,
    /// because by then the process holds what the agent has learned.
    /// </para>
    /// <para>
    /// <b>Only while the line accepted is still the automatic one.</b> A person who corrected it before the refresh was
    /// sent has made it theirs, and a refresh for it is a later change like any other.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<(long Question, AgentName Agent)>> RefreshDueAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<(long Id, string Agent)>(
            new CommandDefinition(
                """
                SELECT q.id, q.agent
                  FROM ownership_questions AS q
                  JOIN agent_ownership_accepted AS o ON o.agent = q.agent AND o.source = 'automatic'
                 WHERE q.state = 'accepted'
                   AND q.restart_sent = 0
                   AND q.id = (SELECT max(id) FROM ownership_questions WHERE agent = q.agent)
                   AND NOT EXISTS (SELECT 1 FROM ownership_questions r WHERE r.agent = q.agent AND r.restart_sent = 1)
                 ORDER BY q.id;
                """,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => (r.Id, AgentName.Create(r.Agent)))];
    }

    /// <summary>Records that an agent's node was asked to refresh it, so it is never asked again.</summary>
    /// <param name="question">The question whose acceptance the refresh was for.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was recorded.</returns>
    public async Task<bool> RefreshSentAsync(
        long question,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE ownership_questions SET restart_sent = 1 WHERE id = @Id AND restart_sent = 0;",
                new { Id = question },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>The project's Auto Accept switch.</summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether answers become lines with no person involved. On when never set.</returns>
    /// <remarks>
    /// A store with no row reads as on: Auto Accept is on by default.
    /// </remarks>
    public async Task<bool> AutoAcceptAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT auto_accept FROM facilitation WHERE id = 1;",
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) is not 0L;

    /// <summary>Sets the project's Auto Accept switch.</summary>
    /// <param name="on">Whether answers become lines with no person involved.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The switch as now set.</returns>
    public async Task<bool> SetAutoAcceptAsync(
        bool on,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO facilitation (id, auto_accept, changed_utc) VALUES (1, @On, @When)
                ON CONFLICT(id) DO UPDATE SET auto_accept = excluded.auto_accept, changed_utc = excluded.changed_utc;
                """,
                new { On = on ? 1 : 0, When = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return on;
    }

    private async Task<string?> SettleAsync(
        AgentName agent,
        string state,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken) =>
        await _connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                """
                -- The reason goes with the waiting: storage refuses a reason on anything but a waiting answer, because a
                -- reason that is no longer true is the one a person would read next.
                UPDATE ownership_questions
                   SET state = @State, settled_utc = @When, waiting_because = NULL
                 WHERE agent = @Agent AND state IN ('asked', 'asked_back', 'answered', 'waiting')
                RETURNING conversation_id;
                """,
                new { Agent = agent.Value, State = state, When = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>A reason, held to what storage admits.</summary>
    /// <remarks>
    /// Cut rather than refused, because the reason is the surface's own sentence or a refusal quoted from elsewhere, and
    /// an answer that could not wait because its reason was long would be lost for a formatting detail.
    /// </remarks>
    private static string Bounded(string because)
    {
        var text = string.IsNullOrWhiteSpace(because) ? "no reason was given" : because.Trim();

        return text.Length <= 500 ? text : string.Concat(text.AsSpan(0, 499), "…");
    }

    internal static QuestionState Parse(string state) => state switch
    {
        "asked" => QuestionState.Asked,
        "asked_back" => QuestionState.AskedBack,
        "answered" => QuestionState.Answered,
        "waiting" => QuestionState.Waiting,
        "accepted" => QuestionState.Accepted,
        "discarded" => QuestionState.Discarded,
        _ => throw new InvalidOperationException($"Unrecognised question state '{state}'.")
    };

    private static string Format(QuestionState state) => state switch
    {
        QuestionState.Asked => "asked",
        QuestionState.AskedBack => "asked_back",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private sealed class OpenQuestionRow
    {
        public long Id { get; init; }

        public string Agent { get; init; } = "";

        public string Conversation { get; init; } = "";

        public long Exchange { get; init; }

        public string State { get; init; } = "";

        public string ExchangeState { get; init; } = "";

        public string OwedBy { get; init; } = "";

        public string? LastType { get; init; }

        public string? LastPayload { get; init; }
    }

    private sealed class QuestionRow
    {
        public string Agent { get; init; } = "";

        public string Conversation { get; init; } = "";

        public long Exchange { get; init; }

        public string State { get; init; } = "";

        public string? Answer { get; init; }

        public string? Flattened { get; init; }

        public string? WaitingBecause { get; init; }

        public string AskedUtc { get; init; } = "";

        public string? SettledUtc { get; init; }

        public OwnershipQuestion ToDomain() => new(
            AgentName.Create(Agent),
            Conversation,
            Exchange,
            Parse(State),
            Answer,
            Flattened,
            WaitingBecause,
            ExchangeRow.ParseTimestamp(AskedUtc),
            SettledUtc is null ? null : ExchangeRow.ParseTimestamp(SettledUtc));
    }
}

/// <summary>A question still waiting on the agent or on a person's reply, and what the agent last said in it.</summary>
/// <param name="Id">The question.</param>
/// <param name="Agent">Who was asked.</param>
/// <param name="Conversation">Its conversation.</param>
/// <param name="Exchange">Its Request's exchange.</param>
/// <param name="State">Asked or asked back.</param>
/// <param name="ExchangeClosed">Whether the exchange has closed.</param>
/// <param name="OwedBy">Who holds the next move on the exchange.</param>
/// <param name="LastType">The type of the latest message the agent sent in it, if any.</param>
/// <param name="LastPayload">That message's payload, if any.</param>
public sealed record OpenQuestion(
    long Id,
    AgentName Agent,
    string Conversation,
    long Exchange,
    QuestionState State,
    bool ExchangeClosed,
    AgentName OwedBy,
    MessageType? LastType,
    string? LastPayload);
