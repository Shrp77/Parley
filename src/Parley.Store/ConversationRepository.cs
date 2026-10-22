using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Conversations, the closure poll of SPEC.md §5.0, and closing a conversation.
/// </summary>
/// <remarks>
/// <para>
/// Membership is derived, never declared: a row appears here because a participant became party to an
/// exchange in the conversation. Nobody joins one by asking to.
/// </para>
/// <para>
/// What this type reports is deliberately narrow. §5.0 makes the poll strictly bilateral, so nothing
/// here returns a roster, a tally or another participant's position to anything an agent can reach.
/// </para>
/// </remarks>
public sealed class ConversationRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>The longest reason a person's close carries, in characters.</summary>
    /// <remarks>
    /// Shortened rather than refused (§14): a close that cannot be stored is a close nobody is told
    /// about, and the reason is the least important part of it.
    /// </remarks>
    public const int MaxReasonCharacters = 1000;

    /// <summary>Loads a conversation.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The conversation, or <see langword="null"/> if none has that identifier.</returns>
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
                       closed_utc AS ClosedUtc, closed_by AS ClosedBy
                  FROM conversations
                 WHERE id = @Id;
                """,
                new { Id = id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>Opens a conversation if it does not exist yet.</summary>
    /// <param name="conversationId">The identifier.</param>
    /// <param name="nowUtc">The instant it started.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// Idempotent, because two senders may start the same named conversation at once and neither should
    /// fail. Called from admission, inside the transaction that admits the Request starting it.
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

    /// <summary>Records that two participants are party to a conversation, and voids any poll in progress.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="first">One participant.</param>
    /// <param name="second">The other.</param>
    /// <param name="nowUtc">The instant of joining.</param>
    /// <param name="transaction">The transaction admitting the exchange.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// Both facts come from one act. A participant is one because it is party to an exchange in the
    /// conversation, and a Request opened in a conversation discards any poll in progress and every vote
    /// cast in it (§5.0), because a participant that was finished may not be once somebody reopens the
    /// subject.
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

    /// <summary>Records that an agent that placed a file is party to the file's conversation.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="participant">The agent that placed the file.</param>
    /// <param name="nowUtc">The instant of joining.</param>
    /// <param name="transaction">The transaction recording the placement.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// <para>
    /// The one way into a conversation that is not an exchange (SPEC.md §5.0). A conversation started by a file has
    /// no exchange for membership to be derived from, and the agent that started it must be able to add a second
    /// file by naming it and take part in closing it.
    /// </para>
    /// <para>
    /// Unlike joining through a Request, this voids no poll: a file is not new work reopening the subject, and a
    /// participant that placed one has said nothing about whether it is finished.
    /// </para>
    /// </remarks>
    public Task JoinByPlacingAsync(
        string conversationId,
        AgentName participant,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
                VALUES (@Conversation, @Agent, @When)
                ON CONFLICT (conversation_id, agent) DO NOTHING;
                """,
                new { Conversation = conversationId, Agent = participant.Value, When = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken));
    }

    /// <summary>Discards a poll in progress and every vote cast in it.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if a poll was in progress and has been discarded.</returns>
    /// <remarks>
    /// A no-op unless the conversation is <see cref="ConversationState.Closing"/>, so it can never be a
    /// back door to reopening a closed one.
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
                "UPDATE conversations SET state = 'OPEN' WHERE id = @Conversation AND state = 'CLOSING';",
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected > 0)
        {
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
        }

        scope.Complete();
        return affected > 0;
    }

    /// <summary>Reports whether a participant is party to a conversation.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="agent">The participant.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> if it has been party to an exchange in it.</returns>
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

    /// <summary>Opens a poll, putting the closure question to every participant worth asking.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="nowUtc">The instant the poll opened.</param>
    /// <param name="transaction">The transaction to write within.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// <para>
    /// Returns nothing, deliberately: the obvious return value is how many were asked, which is exactly
    /// the tally §5.0 forbids disclosing.
    /// </para>
    /// <para>
    /// <b>Only participants holding an open exchange are asked</b> (§5.0). One whose exchanges have all
    /// closed has said what it had to say, and starting it to ask whether it is finished would buy a
    /// turn for nothing. A retired participant is not asked, because its judgement lapsed with it.
    /// </para>
    /// <para>
    /// The proposer gets no exemption. It may propose, and a proposer that holds nothing open has
    /// already said the only thing the poll would ask it.
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

        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE conversations SET state = 'CLOSING' WHERE id = @Conversation AND state = 'OPEN';",
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
                   AND agent NOT IN (SELECT name FROM agents WHERE status = 'retired')
                   AND agent IN (SELECT requester FROM exchanges
                                  WHERE conversation_id = @Conversation AND state = 'OPEN'
                                 UNION
                                 SELECT receiver FROM exchanges
                                  WHERE conversation_id = @Conversation AND state = 'OPEN');
                """,
                new { Conversation = conversationId, When = ExchangeRow.FormatTimestamp(nowUtc) },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
    }

    /// <summary>Records a participant's answer to a poll it was asked.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="agent">The participant answering.</param>
    /// <param name="vote">Its answer.</param>
    /// <param name="nowUtc">The instant it answered.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the vote was recorded.</returns>
    /// <remarks>
    /// Refuses a participant never polled and a conversation not polling, in the <c>WHERE</c> clause
    /// rather than by trusting the caller. A later vote overwrites an earlier one, so a participant that
    /// declined and has since been satisfied can say so.
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

    /// <summary>Closes a conversation by consensus if its poll has been satisfied.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="nowUtc">The instant to close at.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> if the conversation is now closed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every participant asked must have agreed.</b> A decline blocks outright, and so does silence:
    /// a participant that has not answered has not agreed (§5.0). A retired participant counts for
    /// nothing either way.
    /// </para>
    /// <para>
    /// Work still open when the participants agree ends <c>closed</c>, with the broker's Result to each
    /// participant holding it, exactly as a person's close does (§14).
    /// </para>
    /// </remarks>
    public async Task<bool> TryConcludeAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        using var scope = WriteScope.Begin(_connection, transaction);

        var polling = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM conversations WHERE id = @Conversation AND state = 'CLOSING';",
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

        var blocking = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT count(*)
                  FROM conversation_participants p
                 WHERE p.conversation_id = @Conversation
                   AND p.polled_utc IS NOT NULL
                   AND p.agent NOT IN (SELECT name FROM agents WHERE status = 'retired')
                   AND (p.vote IS NULL OR p.vote = 'not_done');
                """,
                new { Conversation = conversationId },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (!polling || blocking > 0)
        {
            scope.Complete();
            return false;
        }

        var closed = await CloseAsync(
            conversationId, ConversationCloser.Consensus, reason: null, nowUtc, scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return closed;
    }

    /// <summary>Closes a conversation on the person's word, regardless of its participants' votes.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="reason">Why, told to every participant holding open work; or <see langword="null"/>.</param>
    /// <param name="nowUtc">The instant of closure.</param>
    /// <param name="transaction">A transaction to enlist in, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if this call closed it.</returns>
    /// <remarks>
    /// §10.2's <c>force_conclude</c>, and an operator operation only. It closes a conversation in any
    /// state short of closed: a person ending work should not have to wait for somebody to propose it.
    /// </remarks>
    public Task<bool> ForceConcludeAsync(
        string conversationId,
        string? reason,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return CloseAsync(
            conversationId, ConversationCloser.Person, reason, nowUtc, transaction, cancellationToken);
    }

    /// <summary>
    /// Closes a conversation, and ends every exchange still open in it with the broker's <c>closed</c>
    /// Result to each participant holding it — in one write.
    /// </summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="closer">Who closed it.</param>
    /// <param name="reason">The person's reason, if one was given.</param>
    /// <param name="nowUtc">The instant of closure.</param>
    /// <param name="transaction">A transaction to enlist in, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if this call closed it.</returns>
    /// <remarks>
    /// <para>
    /// <b>The close and the notices are one write</b> (§14). An exchange is closable exactly once, so a
    /// close that committed without its notices would leave a participant working on something ended,
    /// with nothing ever revisiting it.
    /// </para>
    /// <para>
    /// Both parties are told: the receiver so it stops, the requester so it knows its work was not done.
    /// Each notice is a Result carrying an outcome of <c>closed</c> for every item the Request carried,
    /// attributed to <c>parley:broker</c>, which the schema requires of a closed outcome and forbids of
    /// anything else (§9.3). It says who closed it and nothing a participant wrote, so it cannot exceed
    /// the message cap by quoting one.
    /// </para>
    /// </remarks>
    private async Task<bool> CloseAsync(
        string conversationId,
        ConversationCloser closer,
        string? reason,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var scope = WriteScope.Begin(_connection, transaction);

        var closed = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE conversations
                   SET state = 'CLOSED', closed_utc = @When, closed_by = @By
                 WHERE id = @Conversation AND state <> 'CLOSED';
                """,
                new
                {
                    Conversation = conversationId,
                    When = ExchangeRow.FormatTimestamp(nowUtc),
                    By = closer == ConversationCloser.Person ? "person" : "consensus"
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

        if (!closed)
        {
            scope.Complete();
            return false;
        }

        var exchanges = new ExchangeRepository(_connection);
        var messages = new MessageRepository(_connection);

        var open = await exchanges.GetOpenInConversationAsync(conversationId, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        foreach (var exchange in open)
        {
            var payload = ClosedResult(conversationId, closer, reason);

            await messages.AppendAsync(
                exchange.Id, MessageType.Result, exchange.Receiver, exchange.Requester, Responder.Broker,
                payload, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

            await messages.AppendAsync(
                exchange.Id, MessageType.Result, exchange.Requester, exchange.Receiver, Responder.Broker,
                payload, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

            if (!exchange.CloseWithConversation(nowUtc).Succeeded
                || !await exchanges.SaveAsync(exchange, scope.Transaction, cancellationToken).ConfigureAwait(false))
            {
                // Unreachable under the write lock this transaction holds; thrown so the whole close
                // rolls back rather than committing a conversation closed around work left open.
                throw new InvalidOperationException(
                    $"Exchange {exchange.Id} changed while conversation '{conversationId}' was being closed.");
            }
        }

        scope.Complete();
        return true;
    }

    private static string ClosedResult(
        string conversationId, ConversationCloser closer, string? reason)
    {
        var text = closer == ConversationCloser.Person
            ? $"The person running the deployment closed conversation {conversationId} before this was finished."
            : $"The participants agreed conversation {conversationId} was finished before this was.";

        if (!string.IsNullOrWhiteSpace(reason))
        {
            var trimmed = reason.Trim();
            text += " Their reason: " + (trimmed.Length > MaxReasonCharacters
                ? string.Concat(trimmed.AsSpan(0, MaxReasonCharacters), "...")
                : trimmed);
        }

        var payload = Render(text);

        // A long reason could still pass the cap. The notice matters more than its wording, so the
        // reason goes before the close does.
        return payload.Length <= MessageLimits.MaxPayloadCharacters ? payload : Render("Closed.");

        string Render(string said) => JsonSerializer.Serialize(new
        {
            outcomes = new[] { new { id = PayloadShape.WholeRequest, outcome = "closed", text = said } },
            closed_by = closer == ConversationCloser.Person ? "person" : "consensus"
        });
    }

    /// <summary>Lists every conversation with a poll in progress.</summary>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifiers of conversations currently polling.</returns>
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
    /// The participants a closure poll is waiting on once nothing else is: shared by the flags and the wake.
    /// </summary>
    /// <remarks>
    /// Polled, silent and not retired, in a conversation still closing that has no open exchange and no live
    /// decline. §5.0 asks such a participant again and shows it to the person, so a participant that
    /// finished its part and never voted does not leave the conversation closing with nobody told.
    /// </remarks>
    internal const string SilentVoters =
        """
          FROM conversation_participants p
          JOIN conversations c ON c.id = p.conversation_id
         WHERE c.state = 'CLOSING'
           AND p.polled_utc IS NOT NULL
           AND p.vote IS NULL
           AND p.agent NOT IN (SELECT name FROM agents WHERE status = 'retired')
           AND NOT EXISTS (SELECT 1 FROM exchanges x
                            WHERE x.conversation_id = c.id AND x.state = 'OPEN')
           AND NOT EXISTS (SELECT 1 FROM conversation_participants d
                            WHERE d.conversation_id = c.id
                              AND d.vote = 'not_done'
                              AND d.agent NOT IN (SELECT name FROM agents WHERE status = 'retired'))
        """;

    /// <summary>The participants one conversation is waiting on for nothing but a vote.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// Each one and when its poll began, ordered; empty unless the conversation is otherwise ready to close. The
    /// instant identifies the poll, so a caller can ask each participant again once for each poll.
    /// </returns>
    public async Task<IReadOnlyList<(AgentName Agent, DateTimeOffset PolledUtc)>> GetSilentVotersAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var rows = await _connection.QueryAsync<VoterRow>(
            new CommandDefinition(
                $"""
                SELECT p.agent AS Agent, p.polled_utc AS PolledUtc
                {SilentVoters}
                   AND c.id = @Conversation
                 ORDER BY p.agent;
                """,
                new { Conversation = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => (AgentName.Create(r.Agent), ExchangeRow.ParseTimestamp(r.PolledUtc)))];
    }

    /// <summary>Which polls a participant still owes a vote on, shared by the read and the count.</summary>
    private const string OutstandingPolls =
        """
          FROM conversation_participants p
          JOIN conversations c ON c.id = p.conversation_id
         WHERE p.agent = @Agent
           AND c.state = 'CLOSING'
           AND p.polled_utc IS NOT NULL
           AND p.vote IS NULL
        """;

    /// <summary>How many closure polls a participant has not voted on.</summary>
    /// <param name="agent">The participant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of polls waiting on it.</returns>
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

    /// <summary>Lists the closure polls outstanding against one participant.</summary>
    /// <param name="agent">The participant collecting its inbox.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The polls it has been asked and not yet answered.</returns>
    /// <remarks>
    /// Surfaced through the inbox rather than as a message, because §5 admits three message types and a
    /// fourth carrying the broker's questions would make the broker a participant others address. Shown
    /// on every collection until answered, which is how the silent participant is asked again.
    /// </remarks>
    public async Task<IReadOnlyList<ClosurePoll>> GetOutstandingPollsAsync(
        AgentName agent,
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

        return [.. rows.Select(r => new ClosurePoll(r.ConversationId, ExchangeRow.ParseTimestamp(r.PolledUtc)))];
    }

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

    private sealed class VoterRow
    {
        public string Agent { get; init; } = "";

        public string PolledUtc { get; init; } = "";
    }

    /// <summary>Lists conversations for the operator listing of §10.2, most recent activity first.</summary>
    /// <param name="participant">Restrict to conversations this participant is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="limit">How many to return. The caller is expected to have bounded this.</param>
    /// <param name="beforeLastActivityUtc">Resume point: only those last active strictly before it.</param>
    /// <param name="beforeId">The identifier at that resume point, breaking ties the instant alone cannot.</param>
    /// <param name="includeArchived">Whether to list archived conversations as well.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One page of summaries.</returns>
    /// <remarks>
    /// <para>
    /// Activity is read from the messages, because <c>opened_utc</c> says when a conversation started
    /// and nothing else says when anybody last used it.
    /// </para>
    /// <para>
    /// Paged by last activity rather than by offset, and the resume carries the identifier too: the
    /// Windows clock ticks about every 15.6ms, so two participants writing at once share a timestamp, and
    /// a resume on the instant alone would skip one of them with nothing saying so.
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
                WITH messaged AS (
                    SELECT conversation_id      AS id,
                           count(*)             AS messages,
                           min(created_utc)     AS first_utc,
                           max(created_utc)     AS last_utc
                      FROM messages
                     GROUP BY conversation_id
                ),
                -- A conversation started by placing a file has no message (SPEC.md §5.0), and is still a line of work
                -- a person must be able to find: its activity is when it opened, until a message says otherwise. One
                -- that was named and never used — no message, no participant, no file — still does not appear.
                activity AS (
                    SELECT c.id                                   AS id,
                           coalesce(m.messages, 0)                AS messages,
                           coalesce(m.first_utc, c.opened_utc)    AS first_utc,
                           coalesce(m.last_utc, c.opened_utc)     AS last_utc
                      FROM conversations c
                      LEFT JOIN messaged m ON m.id = c.id
                     WHERE m.id IS NOT NULL
                        OR EXISTS (SELECT 1 FROM conversation_participants p WHERE p.conversation_id = c.id)
                        OR EXISTS (SELECT 1 FROM shared_files f WHERE f.conversation_id = c.id)
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

            // By the reservation and by nothing else: a payload or a name that merely looks like an item's marks no
            // conversation as one (SPEC.md §5.7).
            var reserved = await new RoadmapRepository(_connection)
                .ReservationOfAsync(row.Id, cancellationToken).ConfigureAwait(false);

            summaries.Add(new ConversationSummary(
                row.Id,
                [.. participants.Select(AgentName.Create)],
                row.Messages,
                ExchangeRow.ParseTimestamp(row.FirstActivityUtc),
                ExchangeRow.ParseTimestamp(row.LastActivityUtc),
                row.HasOpenExchange,
                row.Archived,
                reserved is { } r ? new RoadmapItemRef(r.RoadmapId, r.RoadmapNumber, r.Title, r.ItemNumber, r.Removed) : null));
        }

        return summaries;
    }

    /// <summary>Counts the archived conversations a listing with the same filters would have omitted.</summary>
    /// <param name="participant">Restrict to conversations this participant is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those last active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those last active at or before this instant, if given.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many were left out.</returns>
    /// <remarks>
    /// Across the whole filtered set rather than per page, and part of the listing contract: archiving
    /// does not stop a conversation receiving messages, so a surface that could not tell archived ones
    /// existed would be hiding traffic.
    /// </remarks>
    public async Task<int> CountArchivedAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                WITH messaged AS (
                    SELECT conversation_id      AS id,
                           max(created_utc)     AS last_utc
                      FROM messages
                     GROUP BY conversation_id
                ),
                activity AS (
                    SELECT c.id AS id, coalesce(m.last_utc, c.opened_utc) AS last_utc
                      FROM conversations c
                      LEFT JOIN messaged m ON m.id = c.id
                     WHERE m.id IS NOT NULL
                        OR EXISTS (SELECT 1 FROM conversation_participants p WHERE p.conversation_id = c.id)
                        OR EXISTS (SELECT 1 FROM shared_files f WHERE f.conversation_id = c.id)
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

    /// <summary>Puts a conversation out of the ordinary listing, or brings it back.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="archived">Whether it should be archived.</param>
    /// <param name="nowUtc">The current instant, recorded when archiving.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// The state column is not touched: archiving is orthogonal to closure. The <c>IS NULL</c> guards make
    /// it idempotent, so archiving twice does not move the timestamp to an event that did not happen.
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
                new { Conversation = conversationId, When = ExchangeRow.FormatTimestamp(nowUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return changed > 0;
    }

    /// <summary>Reads what is left of a conversation that was destroyed.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when no conversation of that name was destroyed.</returns>
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
    /// <param name="nowUtc">The current instant, recorded on what is left.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What was destroyed, per table.</returns>
    /// <remarks>
    /// <para>
    /// <b>Foreign keys deferred to commit, and deliberately not cascading</b> — the shape of
    /// <c>AgentRepository.PurgeAsync</c>. A cascade deletes whatever it reaches and reports nothing, so a
    /// reference added later is swept silently; a deferred check refuses at commit and rolls everything
    /// back (§14).
    /// </para>
    /// <para>
    /// The record is written before the deletes, because the participants and the message count are read
    /// from rows about to go. It holds facts and no content, and keeps the identifier taken.
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

        await ExecuteAsync("PRAGMA defer_foreign_keys = ON;").ConfigureAwait(false);

        var toll = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var table in new[] { "result_sources", "result_items", "conversation_cycles" })
        {
            await RazeAsync(
                table,
                // Interpolated because a table name cannot be a parameter; the name comes from the
                // array above and nowhere else.
                $"""
                DELETE FROM {table}
                 WHERE exchange_id IN (SELECT id FROM exchanges WHERE conversation_id = @Conversation);
                """).ConfigureAwait(false);
        }

        await RazeAsync("messages", "DELETE FROM messages WHERE conversation_id = @Conversation;")
            .ConfigureAwait(false);

        await RazeAsync("exchanges", "DELETE FROM exchanges WHERE conversation_id = @Conversation;")
            .ConfigureAwait(false);

        // Its files (SPEC.md §14): every agent that fetched one is told it is gone before the records that say who
        // fetched what are deleted, and the rows are reported like every other. The folder on disk is the server's
        // to delete; the notices stay, having no key to the conversation, so the agents are still told.
        await new FileNoticeRepository(_connection).RaiseGoneForConversationsAsync(
            ConversationScope.One,
            new { Conversation = conversationId, When = ExchangeRow.FormatTimestamp(nowUtc) },
            scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        await RazeAsync("file_fetches", "DELETE FROM file_fetches WHERE conversation_id = @Conversation;")
            .ConfigureAwait(false);

        await RazeAsync("shared_files", "DELETE FROM shared_files WHERE conversation_id = @Conversation;")
            .ConfigureAwait(false);

        await RazeAsync(
            "conversation_folders",
            "DELETE FROM conversation_folders WHERE conversation_id = @Conversation;")
            .ConfigureAwait(false);

        await RazeAsync(
            "conversation_participants",
            "DELETE FROM conversation_participants WHERE conversation_id = @Conversation;")
            .ConfigureAwait(false);

        await ExecuteAsync(
            """
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES (@Conversation, @When, @Messages, @Participants);
            """).ConfigureAwait(false);

        // Not tolled: the toll is an account of collateral, and the conversation is what was asked for.
        await ExecuteAsync("DELETE FROM conversations WHERE id = @Conversation;").ConfigureAwait(false);

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
    /// Exactly <see cref="ExchangeRow.FormatTimestamp(DateTimeOffset)"/>'s rendering, because these values
    /// are compared as text: a <see cref="DateTime"/> rendered with "O" ends in <c>Z</c> and sorts after
    /// the same instant ending in <c>+00:00</c>.
    /// </remarks>
    private static string? Format(DateTimeOffset? instant) =>
        instant is null ? null : ExchangeRow.FormatTimestamp(instant.Value);

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

        public string? ClosedBy { get; init; }

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
            ClosedUtc is null ? null : ExchangeRow.ParseTimestamp(ClosedUtc),
            ClosedBy switch
            {
                null => null,
                "consensus" => ConversationCloser.Consensus,
                "person" => ConversationCloser.Person,
                _ => throw new InvalidOperationException($"Unrecognised closer '{ClosedBy}'.")
            });
    }
}
