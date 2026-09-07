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
    /// Opens a conversation if no agent has named it yet.
    /// </summary>
    /// <param name="conversationId">The identifier being declared.</param>
    /// <param name="nowUtc">The instant it was first named.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// Idempotent, because two agents may name the same new identifier at once and neither
    /// should lose. Agents never create a conversation explicitly: SPEC.md §5.0 makes it
    /// whatever its participants said belonged together, so it comes into being as a side effect
    /// of the first message that names it.
    /// <para>
    /// Called from admission and from a notification, which is the whole reason it is a method.
    /// A notification has no exchange and §5.0 therefore has it declare its own conversation —
    /// so both paths mint one, and two copies of that INSERT would be two chances to get the
    /// initial state wrong.
    /// </para>
    /// </remarks>
    public async Task EnsureOpenAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO conversations (id, state, opened_utc)
                VALUES (@Id, 'OPEN', @Opened)
                ON CONFLICT (id) DO NOTHING;
                """,
                new { Id = conversationId, Opened = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
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
    /// restart into a weapon — the attach sequence orphans the debts of whoever attached, so a
    /// participant proposing closure straight afterwards could find nobody left to ask and
    /// conclude the discussion single-handed. A probe closed a live discussion that way in three
    /// calls, back when a single attach orphaned every open exchange at once.
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
    /// Which polls an agent still owes a vote on, shared by the read and the count of it.
    /// </summary>
    /// <remarks>
    /// Shared rather than written twice. The transport pushes a notice saying how much is
    /// waiting and the adapter then drains to find out what it is, so a count computed from a
    /// looser predicate than the read uses reports work the read will not hand over -- and the
    /// adapter wakes, asks, receives nothing, and cannot tell that from a race worth retrying.
    /// </remarks>
    private const string OutstandingPolls =
        """
          FROM conversation_participants p
          JOIN conversations c ON c.id = p.conversation_id
         WHERE p.agent = @Agent
           AND c.state = 'CLOSING'
           AND p.polled_utc IS NOT NULL
           AND p.vote IS NULL
        """;

    /// <summary>
    /// How many closure polls an agent has not voted on.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of polls awaiting this agent.</returns>
    /// <remarks>
    /// Takes no silence window, unlike <see cref="GetOutstandingPollsAsync"/>. The window says
    /// when an unanswered poll lapses into consent, which the caller needs in order to say when
    /// each expires; it does not decide which polls are outstanding, so it has no part in
    /// counting them.
    /// </remarks>
    public async Task<int> CountOutstandingPollsAsync(
        AgentName agent,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"""
                SELECT count(*)
                {OutstandingPolls};
                """,
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

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
                $"""
                SELECT p.conversation_id AS ConversationId, p.polled_utc AS PolledUtc
                {OutstandingPolls}
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

    /// <summary>
    /// Lists conversations for the operator listing of SPEC.md §10.2, most recent activity first.
    /// </summary>
    /// <param name="participant">Restrict to conversations this agent is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="limit">How many to return. The caller is expected to have bounded this.</param>
    /// <param name="beforeLastActivityUtc">Resume point: only those last active strictly before it.</param>
    /// <param name="beforeId">
    /// The identifier at that resume point, breaking ties the instant alone cannot.
    /// </param>
    /// <param name="includeArchived">Whether to list archived conversations as well.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One page of summaries.</returns>
    /// <remarks>
    /// <para>
    /// Activity is read from the messages rather than from the conversation's own timestamps,
    /// because <c>opened_utc</c> says when an agent first named it and nothing says when anybody
    /// last used it. An operator scanning for "what happened this afternoon" is asking the second
    /// question.
    /// </para>
    /// <para>
    /// Participants come from <c>conversation_participants</c>, which §5.0 fills from the
    /// exchanges. They are derived and are not evidence of agreement: an identifier is a claim by
    /// its sender, so an agent may name a discussion it was not part of and thereby join it.
    /// </para>
    /// <para>
    /// Paged by last activity rather than by offset. An offset re-reads rows that may have moved
    /// between pages, which for a log that only grows means the same conversation appearing twice
    /// and another never appearing at all.
    /// </para>
    /// <para>
    /// The resume carries the identifier as well as the instant, because the order breaks ties on
    /// the identifier and a resume that did not would skip every remaining member of a group
    /// sharing one instant. That is not a contrived case: the Windows system clock ticks about
    /// every 15.6ms, so two agents answering at the same moment are written with the same
    /// timestamp — and the conversation that vanishes is simply absent, with nothing saying so.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        int limit = 50,
        DateTimeOffset? beforeLastActivityUtc = null,
        string? beforeId = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var rows = await _connection.QueryAsync<ConversationSummaryRow>(
            new CommandDefinition(
                """
                WITH activity AS (
                    SELECT conversation_id      AS id,
                           count(*)             AS messages,
                           min(created_utc)     AS first_utc,
                           max(created_utc)     AS last_utc
                      FROM messages
                     WHERE conversation_id IS NOT NULL
                     GROUP BY conversation_id
                )
                SELECT c.id                                       AS Id,
                       a.messages                                 AS Messages,
                       a.first_utc                                AS FirstActivityUtc,
                       a.last_utc                                 AS LastActivityUtc,
                       EXISTS (SELECT 1 FROM exchanges e
                                WHERE e.conversation_id = c.id
                                  AND e.state = 'OPEN')           AS HasOpenExchange,
                       (c.archived_utc IS NOT NULL)               AS Archived
                  FROM conversations c
                  JOIN activity a ON a.id = c.id
                 WHERE (@IncludeArchived OR c.archived_utc IS NULL)
                   AND (@Participant IS NULL
                        OR EXISTS (SELECT 1 FROM conversation_participants p
                                    WHERE p.conversation_id = c.id
                                      AND p.agent = @Participant))
                   AND (@Since IS NULL OR a.last_utc >= @Since)
                   AND (@Until IS NULL OR a.last_utc <= @Until)
                   AND (@Before IS NULL
                        OR a.last_utc < @Before
                        OR (a.last_utc = @Before AND c.id < @BeforeId))
                 ORDER BY a.last_utc DESC, c.id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    Participant = participant?.Value,
                    Since = Format(sinceUtc),
                    Until = Format(untilUtc),
                    Before = Format(beforeLastActivityUtc),
                    BeforeId = beforeId ?? string.Empty,
                    Limit = limit,
                    IncludeArchived = includeArchived
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var summaries = new List<ConversationSummary>();

        foreach (var row in rows)
        {
            var participants = await _connection.QueryAsync<string>(
                new CommandDefinition(
                    """
                    SELECT agent
                      FROM conversation_participants
                     WHERE conversation_id = @Id
                     ORDER BY agent;
                    """,
                    new { row.Id },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            summaries.Add(new ConversationSummary(
                row.Id,
                [.. participants.Select(AgentName.Create)],
                row.Messages,
                ExchangeRow.ParseTimestamp(row.FirstActivityUtc),
                ExchangeRow.ParseTimestamp(row.LastActivityUtc),
                row.HasOpenExchange,
                row.Archived));
        }

        return summaries;
    }

    /// <summary>
    /// Counts the archived conversations a listing with the same filters would have omitted.
    /// </summary>
    /// <param name="participant">Restrict to conversations this agent is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many were left out.</returns>
    /// <remarks>
    /// <para>
    /// Counted across the whole filtered set rather than per page, because it answers <i>are there
    /// archived conversations here</i> and a per-page number would answer something narrower that
    /// looks the same. A caller on page three seeing zero would conclude there were none.
    /// </para>
    /// <para>
    /// It exists because archiving does not stop a conversation receiving messages. Without this
    /// number a surface hiding archived conversations would be hiding traffic, and would have no
    /// way to know it.
    /// </para>
    /// </remarks>
    public async Task<int> CountArchivedAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default)
    {
        return await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                WITH activity AS (
                    SELECT conversation_id      AS id,
                           max(created_utc)     AS last_utc
                      FROM messages
                     WHERE conversation_id IS NOT NULL
                     GROUP BY conversation_id
                )
                SELECT count(*)
                  FROM conversations c
                  JOIN activity a ON a.id = c.id
                 WHERE c.archived_utc IS NOT NULL
                   AND (@Participant IS NULL
                        OR EXISTS (SELECT 1 FROM conversation_participants p
                                    WHERE p.conversation_id = c.id
                                      AND p.agent = @Participant))
                   AND (@Since IS NULL OR a.last_utc >= @Since)
                   AND (@Until IS NULL OR a.last_utc <= @Until);
                """,
                new
                {
                    Participant = participant?.Value,
                    Since = Format(sinceUtc),
                    Until = Format(untilUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Puts a conversation out of the ordinary listing, or brings it back.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="archived">Whether it should be archived.</param>
    /// <param name="nowUtc">The current instant, recorded when archiving.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// <para>
    /// One method for both directions because they are one column, and two would be two places to
    /// get the guard below wrong.
    /// </para>
    /// <para>
    /// <b>The state column is not touched and must not be.</b> Archiving is orthogonal to the
    /// closure lifecycle of SPEC.md §5.0: an operator may file away a conversation that is still
    /// open, and closing one does not file it. Writing to <c>state</c> here would make the first
    /// conversation that was both lose one of the two facts.
    /// </para>
    /// <para>
    /// The <c>IS NULL</c> guards make this idempotent, so archiving twice reports no change rather
    /// than moving the timestamp. An operator who archived something last week and clicks again
    /// has not re-archived it today, and a surface showing the second date would be reporting an
    /// event that did not happen.
    /// </para>
    /// </remarks>
    public async Task<bool> SetArchivedAsync(
        string conversationId,
        bool archived,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var changed = await _connection.ExecuteAsync(
            new CommandDefinition(
                archived
                    ? """
                      UPDATE conversations
                         SET archived_utc = @When
                       WHERE id = @Conversation AND archived_utc IS NULL;
                      """
                    : """
                      UPDATE conversations
                         SET archived_utc = NULL
                       WHERE id = @Conversation AND archived_utc IS NOT NULL;
                      """,
                new
                {
                    Conversation = conversationId,
                    When = ExchangeRow.FormatTimestamp(nowUtc)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return changed > 0;
    }

    /// <summary>Reads what is left of a conversation that was destroyed.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when no such conversation was destroyed.</returns>
    /// <remarks>
    /// <para>
    /// Null and a record are two different answers and both are useful: null means no conversation
    /// of that name was ever destroyed, which -- together with an empty message list -- says the
    /// identifier was never used at all. A record says it was, and when, and how much went.
    /// </para>
    /// <para>
    /// The participants come back as one value because that is how they are stored: a row per
    /// participant would outlive the agents it named and need a cascade of its own, for a table
    /// nothing joins against.
    /// </para>
    /// </remarks>
    public async Task<DestroyedConversation?> GetDestroyedAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var row = await _connection.QuerySingleOrDefaultAsync<DestroyedRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, destroyed_utc AS DestroyedUtc, messages AS Messages,
                       participants AS Participants
                  FROM destroyed_conversations
                 WHERE id = @Conversation;
                """,
                new { Conversation = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new DestroyedConversation(
            row.Id,
            ExchangeRow.ParseTimestamp(row.DestroyedUtc),
            row.Messages,
            [
                .. row.Participants
                    .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    .Where(name => AgentName.TryCreate(name, out _))
                    .Select(AgentName.Create)
            ]);
    }

    /// <summary>Deletes a conversation and everything belonging to it.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="nowUtc">The current instant, recorded on the tombstone.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What was destroyed, per table.</returns>
    /// <remarks>
    /// <para>
    /// <b>Foreign keys deferred to commit, and deliberately not cascading</b> — the same shape as
    /// <c>AgentRepository.PurgeAsync</c> and for the same load-bearing reason. A cascade deletes
    /// whatever it reaches and reports nothing, so a reference added by a later migration is swept
    /// silently, and one added in the wrong place is swept silently too. A deferred check refuses
    /// at commit and rolls the whole thing back, which is what SPEC.md §9.3 means by an invariant
    /// belonging in storage rather than in whichever code happens to be running.
    /// </para>
    /// <para>
    /// <b>The tombstone is written before the deletes.</b> It has to be: the participants and the
    /// message count are read from rows this method is about to remove, and computing them
    /// afterwards would record zero of everything.
    /// </para>
    /// <para>
    /// It holds facts and no content — §9.4 argues that a log kept past its usefulness grows the
    /// blast radius of a leaked database, and keeping message text past a destroy is that argument
    /// inverted. It also keeps the identifier taken, so a new conversation cannot be opened under
    /// the name of one somebody already holds an export of.
    /// </para>
    /// </remarks>
    public async Task<DestroyOutcome> DestroyAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        using var scope = WriteScope.Begin(_connection, transaction);

        var exists = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM conversations WHERE id = @Conversation;",
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

        if (!exists)
        {
            scope.Complete();
            return DestroyOutcome.NoSuchConversation;
        }

        var participants = await _connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT agent
                  FROM conversation_participants
                 WHERE conversation_id = @Conversation
                 ORDER BY agent;
                """,
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var messages = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM messages WHERE conversation_id = @Conversation;",
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // See the remarks: not cascading is the guard, so a table nobody remembered fails the
        // commit rather than leaving a row pointing at a conversation that has gone.
        await ExecuteAsync("PRAGMA defer_foreign_keys = ON;").ConfigureAwait(false);

        var toll = new Dictionary<string, int>(StringComparer.Ordinal);

        await RazeAsync(
            "message_referrals",
            """
            DELETE FROM message_referrals
             WHERE exchange_id IN (SELECT id FROM exchanges
                                    WHERE conversation_id = @Conversation);
            """).ConfigureAwait(false);

        await RazeAsync(
            "messages",
            "DELETE FROM messages WHERE conversation_id = @Conversation;").ConfigureAwait(false);

        await RazeAsync(
            "exchanges",
            "DELETE FROM exchanges WHERE conversation_id = @Conversation;").ConfigureAwait(false);

        await RazeAsync(
            "conversation_participants",
            "DELETE FROM conversation_participants WHERE conversation_id = @Conversation;")
            .ConfigureAwait(false);

        await ExecuteAsync(
            """
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES (@Conversation, @When, @Messages, @Participants);
            """).ConfigureAwait(false);

        // Not tolled, for the reason a purge does not toll the agent's own row: the toll is an
        // account of collateral, and the conversation is the thing that was asked for. Counting it
        // would make an empty toll impossible and cost the report its clearest reading.
        await ExecuteAsync("DELETE FROM conversations WHERE id = @Conversation;")
            .ConfigureAwait(false);

        scope.Complete();

        return DestroyOutcome.Razed(
            [.. toll.Where(t => t.Value > 0)
                    .OrderByDescending(t => t.Value)
                    .ThenBy(t => t.Key, StringComparer.Ordinal)
                    .Select(t => new PurgeToll(t.Key, t.Value))]);

        Task<int> ExecuteAsync(string sql) =>
            _connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        Conversation = conversationId,
                        When = ExchangeRow.FormatTimestamp(nowUtc),
                        Messages = messages,
                        Participants = string.Join(", ", participants)
                    },
                    scope.Transaction,
                    cancellationToken: cancellationToken));

        async Task RazeAsync(string table, string sql)
        {
            var rows = await ExecuteAsync(sql).ConfigureAwait(false);

            toll[table] = toll.GetValueOrDefault(table) + rows;
        }
    }

    /// <summary>Renders an instant the way the store holds one, or null.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The stored form, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <see cref="ExchangeRow.FormatTimestamp(DateTimeOffset)"/>'s rendering, and it has to be exactly that
    /// one because these values are compared as text in SQL. A <see cref="DateTime"/> formatted
    /// with "O" ends in <c>Z</c>; a <see cref="DateTimeOffset"/> ends in <c>+00:00</c>. They
    /// describe the same instant and do not sort the same way — <c>Z</c> orders after <c>+</c> —
    /// so a bound rendered the first way admits rows it was meant to exclude. Found by a paging
    /// test returning a conversation it had already returned.
    /// </remarks>
    private static string? Format(DateTimeOffset? instant) =>
        instant is null ? null : ExchangeRow.FormatTimestamp(instant.Value);

    /// <summary>One row of the operator listing, before its participants are read.</summary>
    private sealed class DestroyedRow
    {
        public string Id { get; init; } = string.Empty;

        public string DestroyedUtc { get; init; } = string.Empty;

        public int Messages { get; init; }

        public string Participants { get; init; } = string.Empty;
    }

    private sealed class ConversationSummaryRow
    {
        public string Id { get; init; } = string.Empty;

        public int Messages { get; init; }

        public string FirstActivityUtc { get; init; } = string.Empty;

        public string LastActivityUtc { get; init; } = string.Empty;

        public bool HasOpenExchange { get; init; }

        public bool Archived { get; init; }
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
