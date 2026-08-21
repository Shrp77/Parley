using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Conversations and the closure poll of SPEC.md §5.0.
/// </summary>
/// <remarks>
/// <para>
/// Membership is derived, never declared: a row appears here because an agent became party to
/// an exchange carrying the identifier. There is no join operation and agents never create a
/// conversation explicitly, because a conversation is not a thing to be set up before use — it
/// is whatever its participants said belonged together.
/// </para>
/// <para>
/// Everything this type exposes is deliberately narrow on what it will report. §5.0 makes the
/// poll strictly bilateral, so nothing here returns a roster, a tally, or another agent's
/// position, and the methods that could have are shaped to make that impossible rather than
/// merely unusual. The broker holds the count; no participant does.
/// </para>
/// </remarks>
public sealed class ConversationRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Loads a conversation.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The conversation, or <see langword="null"/> if no agent has named it.</returns>
    public async Task<ConversationRecord?> GetAsync(
        string id,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var row = await _connection.QuerySingleOrDefaultAsync<ConversationRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, state AS State, opened_utc AS OpenedUtc,
                       closed_utc AS ClosedUtc
                  FROM conversations
                 WHERE id = @Id;
                """,
                new { Id = id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>
    /// Records that two agents are party to a conversation, and voids any poll in progress.
    /// </summary>
    /// <param name="conversationId">The discussion they joined.</param>
    /// <param name="first">One participant.</param>
    /// <param name="second">The other.</param>
    /// <param name="nowUtc">The instant of joining.</param>
    /// <param name="transaction">The transaction admitting the exchange.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// <para>
    /// Called from admission, inside the transaction that writes the exchange, because both
    /// facts come from the same act: SPEC.md §5.0 says an agent is a participant precisely
    /// because it is party to an exchange carrying the identifier, so membership recorded
    /// separately could disagree with the exchange that established it.
    /// </para>
    /// <para>
    /// Voiding the poll happens here for the same reason. §5.0: opening an exchange in a
    /// conversation discards any in-flight poll <em>and every vote already cast</em>, and
    /// closure begins again from nothing. Discarding the votes is the part worth getting right
    /// — an agent that said it was finished may not be, once a peer reopens the subject.
    /// </para>
    /// <para>
    /// Without it the poll has a race with an obvious loser. Only participants holding open
    /// exchanges are polled, so an agent whose exchanges had all closed is not asked; if it
    /// opens a new one while the poll runs, the conversation can conclude around a live
    /// question, which then sits in a closed discussion nobody may add to.
    /// </para>
    /// </remarks>
    public async Task JoinAsync(
        string conversationId,
        AgentName first,
        AgentName second,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var stamp = ExchangeRow.FormatTimestamp(nowUtc);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
                VALUES (@Conversation, @Agent, @When)
                ON CONFLICT (conversation_id, agent) DO NOTHING;
                """,
                new[]
                {
                    new { Conversation = conversationId, Agent = first.Value, When = stamp },
                    new { Conversation = conversationId, Agent = second.Value, When = stamp }
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await VoidPollAsync(conversationId, transaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards a poll in progress and every vote cast in it.
    /// </summary>
    /// <param name="conversationId">The discussion.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if a poll was in progress and has been discarded.</returns>
    /// <remarks>
    /// A no-op unless the conversation is <see cref="ConversationState.Closing"/>. It
    /// deliberately cannot reopen a closed conversation: §5.0 makes closure terminal, and the
    /// state filter is what stops new activity — which is refused on a closed conversation
    /// anyway — from being a back door to reopening one.
    /// </remarks>
    public async Task<bool> VoidPollAsync(
        string conversationId,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        using var scope = WriteScope.Begin(_connection, transaction);

        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversations SET state = 'OPEN'
                 WHERE id = @Conversation AND state = 'CLOSING';
                """,
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected == 0)
        {
            scope.Complete();
            return false;
        }

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversation_participants
                   SET vote = NULL, voted_utc = NULL, polled_utc = NULL
                 WHERE conversation_id = @Conversation;
                """,
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
        return true;
    }

    /// <summary>
    /// Reports whether an agent is party to a conversation.
    /// </summary>
    /// <param name="conversationId">The discussion.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> if the agent has been party to an exchange in it.</returns>
    public async Task<bool> IsParticipantAsync(
        string conversationId,
        AgentName agent,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT count(*) FROM conversation_participants
                 WHERE conversation_id = @Conversation AND agent = @Agent;
                """,
                new { Conversation = conversationId, Agent = agent.Value },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Opens a poll, putting the closure question to every participant worth asking.
    /// </summary>
    /// <param name="conversationId">The discussion.</param>
    /// <param name="nowUtc">The instant the poll opened.</param>
    /// <param name="transaction">The transaction to write within.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// Returns nothing, deliberately. The obvious return value is the number of participants
    /// the question was put to, and that number is exactly the tally SPEC.md §5.0 forbids
    /// disclosing — a count of "three of four agree" gives away the roster size as surely as
    /// naming its members. It was written that way first and the only caller ignored it, which
    /// is the whole hazard: a value nobody needs, sitting on a public method, one careless
    /// caller away from reaching an agent. This class claims to be shaped so that leaking is
    /// impossible rather than merely unusual, and that claim is only worth what the signatures
    /// are worth.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// The polled set is participants with an exchange still live in this conversation, which
    /// is SPEC.md §5.0's rule and not an optimisation. An agent whose exchanges have all closed
    /// has already said what it had to say, and under §14's lazy spawn, polling it would
    /// instantiate an idle agent purely to ask whether it was finished — at this protocol's
    /// scale, most of the roster, woken to answer a question about nothing.
    /// </para>
    /// <para>
    /// Live means <c>OPEN</c> or <c>ORPHANED</c>, and the second is not a detail. §5.0 says
    /// "holding open exchanges" but gives its reason as an agent whose exchanges are all
    /// <em>closed</em>, and an orphan is neither: it is a debt awaiting replay under §14, which
    /// may return it to open at the next inbound traffic. Counting only <c>OPEN</c> made a
    /// restart into a weapon — the reboot sequence orphans everything, so for as long as it
    /// took the first agent to come back, any participant could propose closure, find nobody
    /// left to ask, and conclude the discussion single-handed. A probe closed a live discussion
    /// that way in three calls.
    /// </para>
    /// <para>
    /// The proposer gets no exemption, and an earlier version of this gave it one — stamping it
    /// whether or not it held an open exchange, on the reasoning that it was demonstrably awake
    /// and ought to be able to record its own answer. That defeats the only thing bounding a
    /// gate-crasher. §5.0 admits that an identifier is a claim rather than a verified fact, so
    /// an agent may name a discussion it was not part of and thereby join it, and the cost that
    /// makes acceptable is precisely this: "keeping the veto alive means keeping an exchange
    /// open, which the windows in §6 will eventually close." Exempting the proposer made the
    /// veto free and permanent — a probe held a discussion open for a week from an agent whose
    /// only exchange had closed days earlier.
    /// </para>
    /// <para>
    /// It may still propose. Any participant may, and a proposer that is not polled has already
    /// said the only thing the poll would have asked it. Its vote is simply not recorded, which
    /// <see cref="RecordVoteAsync"/> reports rather than enforcing silently.
    /// </para>
    /// <para>
    /// Agents marked <c>unreachable</c> or <c>retired</c> are excluded. §5.0 lapses their
    /// judgement for the same reason it lapses a decline: one is only worth honouring while the
    /// agent that made it can still act on it, and a discussion held open by a repository that
    /// no longer exists is the churn §14 exists to stop.
    /// </para>
    /// </remarks>
    public async Task BeginPollAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        using var scope = WriteScope.Begin(_connection, transaction);
        var stamp = ExchangeRow.FormatTimestamp(nowUtc);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversations SET state = 'CLOSING'
                 WHERE id = @Conversation AND state = 'OPEN';
                """,
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversation_participants
                   SET polled_utc = @When
                 WHERE conversation_id = @Conversation
                   AND polled_utc IS NULL
                   AND agent NOT IN (SELECT name FROM agents
                                      WHERE status IN ('unreachable', 'retired'))
                   AND agent IN (SELECT asker FROM exchanges
                                  WHERE conversation_id = @Conversation
                                    AND state IN ('OPEN', 'ORPHANED')
                                 UNION
                                 SELECT answerer FROM exchanges
                                  WHERE conversation_id = @Conversation
                                    AND state IN ('OPEN', 'ORPHANED'));
                """,
                new { Conversation = conversationId, When = stamp },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
    }

    /// <summary>
    /// Records a participant's answer to a poll it was asked.
    /// </summary>
    /// <param name="conversationId">The discussion.</param>
    /// <param name="agent">The participant answering.</param>
    /// <param name="vote">Its answer.</param>
    /// <param name="nowUtc">The instant it answered.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the vote was recorded.</returns>
    /// <remarks>
    /// Refuses an agent that was never polled, by matching on <c>polled_utc IS NOT NULL</c>
    /// rather than trusting the caller to have checked. The schema forbids that combination
    /// too; doing it in the <c>WHERE</c> clause means the refusal is a <see langword="false"/>
    /// return rather than a constraint violation the caller has to interpret.
    /// <para>
    /// Refuses a conversation that is not polling, for the same reason and one found later. A
    /// vote against a concluded discussion changes no state — closure is terminal — but it is
    /// recorded, and the row then says a participant declined a closure that had already
    /// happened. The log is what an operator reads when something has gone wrong, so a stored
    /// contradiction is worse than a refused write. The trigger in §9.3 is the backstop.
    /// </para>
    /// <para>
    /// A later vote overwrites an earlier one. An agent that declined and has since been
    /// satisfied must be able to say so, and §5.0's re-ask makes that the ordinary path rather
    /// than an edge case.
    /// </para>
    /// </remarks>
    public async Task<bool> RecordVoteAsync(
        string conversationId,
        AgentName agent,
        Vote vote,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversation_participants
                   SET vote = @Vote, voted_utc = @When
                 WHERE conversation_id = @Conversation
                   AND agent = @Agent
                   AND polled_utc IS NOT NULL
                   AND EXISTS (SELECT 1 FROM conversations
                                WHERE id = @Conversation AND state = 'CLOSING');
                """,
                new
                {
                    Conversation = conversationId,
                    Agent = agent.Value,
                    Vote = FormatVote(vote),
                    When = ExchangeRow.FormatTimestamp(nowUtc)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>
    /// Concludes a conversation if its poll has been satisfied.
    /// </summary>
    /// <param name="conversationId">The discussion.</param>
    /// <param name="nowUtc">The instant to judge silence against.</param>
    /// <param name="silenceWindow">
    /// How long a polled participant may stay silent before it is taken to agree.
    /// </param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> if the conversation is now closed.</returns>
    /// <remarks>
    /// <para>
    /// Three conditions, all from SPEC.md §5.0. A decline blocks outright — closure requires
    /// that no polled participant declined, because a majority that can conclude over an
    /// objection is authority over another agent by a different name. A participant that has
    /// not answered blocks only until its window elapses, after which silence assents; without
    /// that, one unreachable agent could hold a discussion open indefinitely. And a participant
    /// marked <c>unreachable</c> or <c>retired</c> counts for nothing either way, because its
    /// judgement has lapsed with it.
    /// </para>
    /// <para>
    /// The silence window is measured from each participant's own <c>polled_utc</c> rather than
    /// from when the poll opened. They are usually the same instant, and are not when a
    /// participant joined after the poll began — which is possible, since joining is what voids
    /// a poll, and the void leaves the conversation open to be proposed again.
    /// </para>
    /// </remarks>
    public async Task<bool> TryConcludeAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        TimeSpan silenceWindow,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        using var scope = WriteScope.Begin(_connection, transaction);

        // One query for both blocking conditions, so a decline and an unexpired silence cannot
        // be evaluated against different snapshots of the participant set.
        var blocking = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT count(*)
                  FROM conversation_participants p
                 WHERE p.conversation_id = @Conversation
                   AND p.polled_utc IS NOT NULL
                   AND p.agent NOT IN (SELECT name FROM agents
                                        WHERE status IN ('unreachable', 'retired'))
                   AND (p.vote = 'not_done'
                        OR (p.vote IS NULL AND @Deadline < p.polled_utc));
                """,
                new
                {
                    Conversation = conversationId,

                    // Compared as text, which is sound because the format is fixed-width and
                    // zero-padded with a constant offset, so lexical order is chronological.
                    // The same property ExchangeRow relies on everywhere else.
                    Deadline = ExchangeRow.FormatTimestamp(nowUtc - silenceWindow)
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (blocking > 0)
        {
            scope.Complete();
            return false;
        }

        var closed = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversations
                   SET state = 'CLOSED', closed_utc = @When
                 WHERE id = @Conversation AND state = 'CLOSING';
                """,
                new
                {
                    Conversation = conversationId,
                    When = ExchangeRow.FormatTimestamp(nowUtc)
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
        return closed > 0;
    }

    /// <summary>
    /// Closes a conversation regardless of its participants' votes.
    /// </summary>
    /// <param name="conversationId">The discussion to end.</param>
    /// <param name="nowUtc">The instant of closure.</param>
    /// <param name="transaction">
    /// A transaction to enlist in, or <see langword="null"/> to run on its own. Present for the
    /// same reason as on every other write here: an operator surface that ends a discussion will
    /// eventually want to do something else in the same breath.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if this call closed it.</returns>
    /// <remarks>
    /// SPEC.md §10.2's <c>force_conclude</c>, and an operator operation only — see
    /// <c>Broker.ForceConcludeAsync</c> for why it is named apart from the agent-facing one.
    /// Matching on <c>state &lt;&gt; 'CLOSED'</c> rather than on <c>'CLOSING'</c> is deliberate:
    /// an operator ending a discussion should not have to wait for somebody to propose closure
    /// first, and a closure that is already recorded is left with its original instant rather
    /// than being restamped.
    /// </remarks>
    public async Task<bool> ForceConcludeAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var closed = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversations
                   SET state = 'CLOSED', closed_utc = @When
                 WHERE id = @Conversation AND state <> 'CLOSED';
                """,
                new
                {
                    Conversation = conversationId,
                    When = ExchangeRow.FormatTimestamp(nowUtc)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return closed > 0;
    }

    /// <summary>
    /// Lists every conversation with a poll in progress.
    /// </summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifiers of conversations currently polling.</returns>
    /// <remarks>
    /// The sweep's input. Bounded by the number of discussions in flight rather than by
    /// history, since a poll is transient by construction: it either concludes or is voided.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetPollingAsync(
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT id FROM conversations WHERE state = 'CLOSING' ORDER BY id;",
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>
    /// Lists the closure polls outstanding against one agent.
    /// </summary>
    /// <param name="agent">The agent collecting its inbox.</param>
    /// <param name="silenceWindow">How long its silence will be waited for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The polls it has been asked and not yet answered.</returns>
    /// <remarks>
    /// Surfaced through the inbox rather than as a message, because SPEC.md §5 admits four
    /// message types and no others — and a fifth carrying broker questions would also make the
    /// broker a participant that agents address, which it is not.
    /// <para>
    /// Scoped to this agent by the <c>WHERE</c> clause, so there is no shape of result that
    /// could carry another participant's position. A poll it has already answered is omitted:
    /// §5.0 has the broker re-ask when the conversation next falls idle, which is a fresh poll,
    /// not this one shown again.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ClosurePoll>> GetOutstandingPollsAsync(
        AgentName agent,
        TimeSpan silenceWindow,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<PollRow>(
            new CommandDefinition(
                """
                SELECT p.conversation_id AS ConversationId, p.polled_utc AS PolledUtc
                  FROM conversation_participants p
                  JOIN conversations c ON c.id = p.conversation_id
                 WHERE p.agent = @Agent
                   AND c.state = 'CLOSING'
                   AND p.polled_utc IS NOT NULL
                   AND p.vote IS NULL
                 ORDER BY p.polled_utc, p.conversation_id;
                """,
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r =>
        {
            var polled = ExchangeRow.ParseTimestamp(r.PolledUtc);
            return new ClosurePoll(r.ConversationId, polled, polled + silenceWindow);
        }).ToList();
    }

    /// <summary>Formats a vote as the token used in storage.</summary>
    /// <param name="vote">The vote.</param>
    /// <returns>The stored token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="vote"/> is not a defined value.
    /// </exception>
    /// <remarks>
    /// Written out rather than persisting the numeric value, so the schema's <c>CHECK</c> can
    /// validate it and the table stays legible in a SQLite client — the same reasoning as
    /// <see cref="MessageIdentity.FormatType"/>. Private where that one is public, because this
    /// is a storage detail and that is a protocol rule: §13.1 names the hash so independent
    /// implementations agree on what a duplicate is, and nothing outside this file has any
    /// business reading a vote.
    /// </remarks>
    private static string FormatVote(Vote vote) => vote switch
    {
        Vote.Done => "done",
        Vote.NotDone => "not_done",
        _ => throw new ArgumentOutOfRangeException(nameof(vote), vote, null)
    };

    private sealed class PollRow
    {
        public string ConversationId { get; init; } = "";
        public string PolledUtc { get; init; } = "";
    }

    private sealed class ConversationRow
    {
        public string Id { get; init; } = "";
        public string State { get; init; } = "";
        public string OpenedUtc { get; init; } = "";
        public string? ClosedUtc { get; init; }

        public ConversationRecord ToDomain() => new(
            Id,
            State switch
            {
                "OPEN" => ConversationState.Open,
                "CLOSING" => ConversationState.Closing,
                "CLOSED" => ConversationState.Closed,
                _ => throw new InvalidOperationException($"Unrecognised conversation state '{State}'.")
            },
            ExchangeRow.ParseTimestamp(OpenedUtc),
            ClosedUtc is null ? null : ExchangeRow.ParseTimestamp(ClosedUtc));
    }
}
