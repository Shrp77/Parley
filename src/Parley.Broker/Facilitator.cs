using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// The facilitator's work on the store: asking each agent what it owns, reading back what it said, and settling the
/// question (SPEC.md principle 2.11, §10.2).
/// </summary>
/// <param name="connection">The store.</param>
/// <param name="options">The broker's terms, which the facilitator's Request is admitted under like any other.</param>
/// <remarks>
/// <para>
/// <b>A third surface, beside <see cref="Broker"/> and <see cref="OperatorConsole"/> rather than on either.</b>
/// <see cref="Broker"/> is what an attached agent's connection is handed, so a method here would be a facilitator
/// operation every agent could bind; <see cref="OperatorConsole"/> may not send a Request at all. <c>SurfaceBoundaryTests</c>
/// fixes this type's surface as it fixes theirs.
/// </para>
/// <para>
/// <b>It writes through the repositories, not through <see cref="Broker"/></b>, for the reason the broker's own
/// <c>closed</c> result does: <see cref="Broker"/> refuses every <c>parley:</c> attribution, which is what stops an
/// adapter claiming one. What the facilitator writes still passes every rule storage holds — the Request trigger, the
/// open-exchange rules, the admission checks in <c>ExchangeRepository.TryOpenAsync</c> — and it is admitted under the
/// same <see cref="BrokerOptions.ExchangePolicy"/> as an agent's.
/// </para>
/// <para>
/// <b>What it may do is exactly what the constitution lists</b>: ask, send a person's reply, and propose and agree
/// that its own conversation is done. Nothing here assigns, directs, closes or overrides anybody's work, and nothing
/// here touches a conversation the facilitator did not start.
/// </para>
/// </remarks>
public sealed class Facilitator(SqliteConnection connection, BrokerOptions options)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly BrokerOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private OwnershipQuestionRepository Questions => new(_connection);

    /// <summary>Asks every attached agent that has never been asked and has no line.</summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="attached">The agents attached right now, by name.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The agents asked, so the caller can wake each.</returns>
    /// <remarks>
    /// Each question in its own transaction, so one agent a Request cannot be admitted to — retired between the read and
    /// the write, say — leaves the others asked rather than taking them down with it.
    /// </remarks>
    public async Task<IReadOnlyList<AgentName>> AskDueAsync(
        AgentName facilitator,
        IReadOnlyCollection<string> attached,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var due = await Questions.DueAsync(attached, cancellationToken: cancellationToken).ConfigureAwait(false);
        var asked = new List<AgentName>();

        foreach (var agent in due)
        {
            if (await AskAsync(facilitator, agent, nowUtc, cancellationToken).ConfigureAwait(false) is { Asked: true })
            {
                asked.Add(agent);
            }
        }

        return asked;
    }

    /// <summary>Asks one agent what it owns, as a new question.</summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="agent">Who to ask.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was asked, and why not when it was not.</returns>
    /// <remarks>
    /// <para>
    /// <b>One ordinary Request, one conversation of its own, and the question's row, in one transaction</b>, so a
    /// question is never recorded that was not sent, nor sent that was not recorded. A second live question for the
    /// agent is refused by storage's index, which is the rule that makes "never asked twice" survive a restart between
    /// any two of these steps.
    /// </para>
    /// <para>
    /// <b>The words are chosen by the switch as it stands now</b>, because what the question tells the
    /// agent about its answer has to be true when the answer arrives — and a question asked with the switch on promises
    /// the answer becomes the line.
    /// </para>
    /// </remarks>
    public async Task<AskOutcome> AskAsync(
        AgentName facilitator,
        AgentName agent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (Facilitation.Is(agent))
        {
            return AskOutcome.Not("Parley does not ask itself.");
        }

        using var scope = WriteScope.Begin(_connection, ambient: null);
        var questions = Questions;

        if (await questions.LiveAsync(agent, scope.Transaction, cancellationToken).ConfigureAwait(false) is not null)
        {
            scope.Complete();
            return AskOutcome.Not($"'{agent.Value}' already has a question it has not finished with.");
        }

        var conversation = await FreeConversationAsync(agent, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        var autoAccept = await questions.AutoAcceptAsync(scope.Transaction, cancellationToken).ConfigureAwait(false);
        var payload = new System.Text.Json.Nodes.JsonObject { ["ask"] = Facilitation.Question(autoAccept) }.ToJsonString();

        var admission = await new ExchangeRepository(_connection).TryOpenAsync(
            facilitator, agent, nowUtc, MessageIdentity.ComputeRequest(facilitator, agent, payload), conversation,
            _options.ExchangePolicy, requestGroup: null, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (!admission.Succeeded)
        {
            scope.Complete();
            return AskOutcome.Not($"the broker would not open the question: {admission.Rejection}.");
        }

        var exchange = admission.Exchange!.Id;

        await new MessageRepository(_connection).AppendAsync(
            exchange, MessageType.Request, facilitator, agent, Facilitation.Attribution, payload, nowUtc,
            scope.Transaction, cancellationToken).ConfigureAwait(false);

        await questions.InsertAsync(agent, conversation, exchange, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        scope.Complete();
        return AskOutcome.Done(conversation, exchange);
    }

    /// <summary>
    /// Reads what agents said to their open questions, and moves each question to match.
    /// </summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The answers that arrived since the last read, each now recorded as answered and waiting on a decision.</returns>
    /// <remarks>
    /// <para>
    /// <b>An answer is the agent's Result</b>, read from the exchange the facilitator opened and from nowhere else, so an
    /// agent answering an <c>owns-</c> conversation it started itself is not an answer to anything.
    /// A Clarify moves the question to <em>asked back</em>, where it waits for a person's reply; that reply hands
    /// the turn back and the question is <em>asked</em> again.
    /// </para>
    /// <para>
    /// <b>A question whose exchange closed with no Result from the agent was closed by a person</b> — the only other way
    /// an exchange closes (§5.3) — and is settled as discarded, unanswered. Left open it would never be answered and
    /// would stop the agent being asked for ever.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<FacilitatorAnswer>> ReadBackAsync(
        AgentName facilitator,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        using var scope = WriteScope.Begin(_connection, ambient: null);
        var questions = Questions;
        var answers = new List<FacilitatorAnswer>();

        foreach (var open in await questions.OpenAsync(scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            // Read now, so collected now: this pass is Parley's inbox.
            await questions.CollectedAsync(open.Exchange, facilitator, nowUtc, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);

            if (open.ExchangeClosed)
            {
                if (open.LastType != MessageType.Result)
                {
                    _ = await questions.DiscardAsync(open.Agent, nowUtc, scope.Transaction, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var said = PayloadShape.Spoken(open.LastPayload);
                var line = OwnershipStatement.Flatten(said);

                await questions.AnsweredAsync(open.Id, said, line, scope.Transaction, cancellationToken)
                    .ConfigureAwait(false);

                answers.Add(new FacilitatorAnswer(
                    open.Id,
                    open.Agent,
                    open.Conversation,
                    said,
                    line,
                    Blocked: PayloadShape.TryReadOutcomes(open.LastPayload ?? string.Empty, out var outcomes)
                             && outcomes.Any(o => o.Outcome == Outcome.Blocked)));

                continue;
            }

            if (open.State == QuestionState.Asked && open.OwedBy == facilitator)
            {
                await questions.MoveAsync(open.Id, QuestionState.AskedBack, scope.Transaction, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (open.State == QuestionState.AskedBack && open.OwedBy == open.Agent)
            {
                await questions.MoveAsync(open.Id, QuestionState.Asked, scope.Transaction, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        scope.Complete();
        return answers;
    }

    /// <summary>Makes an answer the agent's line, recorded as accepted automatically, and closes its conversation.</summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="answer">The answer, already written to the agent's launch configuration.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was recorded.</returns>
    /// <remarks>
    /// <para>
    /// <b>Called after the file is written, never before</b>: a file with no record is repaired by
    /// the next acceptance, while a record with no file would claim a line the agent will never carry.
    /// </para>
    /// <para>
    /// The line, the question's settlement and the conversation's close are one transaction, so the review never shows a
    /// question accepted with no line, nor a line with its question still waiting.
    /// </para>
    /// </remarks>
    public async Task<bool> AcceptAutomaticallyAsync(
        AgentName facilitator,
        FacilitatorAnswer answer,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var acceptance = new OwnershipAcceptance(
            answer.Line, OwnershipSource.Automatic, answer.Answer.Length, answer.Conversation);

        if (acceptance.Problem() is not null)
        {
            return false;
        }

        using var scope = WriteScope.Begin(_connection, ambient: null);

        // **The question first, and only while it is still the one this pass read.** A person may have recorded a line or
        // asked again since; their decision stands, and nothing below runs.
        if (!await Questions.StillAnsweredAsync(answer.Question, scope.Transaction, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        var (recorded, _) = await new AgentRepository(_connection)
            .RecordOwnershipAsync(answer.Agent, acceptance, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        // The line first, then the question: storage accepts a question only once its line is recorded. Settled by its
        // id, so nothing but the question this pass read can be settled. Not completed on a refusal, so nothing is kept.
        if (!recorded
            || !await Questions.AcceptAnsweredAsync(answer.Question, nowUtc, scope.Transaction, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }
        await AgreeDoneAsync(facilitator, answer.Conversation, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        scope.Complete();
        return true;
    }

    /// <summary>Holds an answer for a person, saying why.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="because">Why it was not accepted automatically, in the words the review shows.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it is now waiting.</returns>
    public async Task<bool> HoldAsync(
        FacilitatorAnswer answer,
        string because,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return await Questions.WaitAsync(answer.Question, because, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Settles an agent's live question after a person recorded its line, and closes the conversation.</summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="agent">Whose line was recorded.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="transaction">The transaction that recorded the line, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether a question was settled.</returns>
    /// <remarks>
    /// A person accepting, editing or writing a line is what settles a waiting answer, and the question has to know —
    /// otherwise it stays waiting beside a line that already answers it, and the agent can never be asked again.
    /// </remarks>
    public async Task<bool> SettledByPersonAsync(
        AgentName facilitator,
        AgentName agent,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = WriteScope.Begin(_connection, transaction);
        var questions = Questions;

        // **Accepted only when Parley has an answer to accept.** A person can write a line, or accept from the transcript,
        // before Parley's next pass has read the agent's answer, and storage refuses a question accepted with no answer.
        // With nothing read yet, the person's line has overtaken the question, so it is settled as discarded.
        var live = await questions.LiveAsync(agent, scope.Transaction, cancellationToken).ConfigureAwait(false);

        var conversation = live is { State: QuestionState.Answered or QuestionState.Waiting }
            ? await questions.AcceptAsync(agent, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false)
            : live is null
                ? null
                : await questions.DiscardAsync(agent, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (conversation is not null)
        {
            await AgreeDoneAsync(facilitator, conversation, nowUtc, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        scope.Complete();
        return conversation is not null;
    }

    /// <summary>The project's Auto Accept switch.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether it is on. On when never set.</returns>
    public Task<bool> AutoAcceptAsync(CancellationToken cancellationToken = default) =>
        Questions.AutoAcceptAsync(cancellationToken: cancellationToken);

    /// <summary>Sets the project's Auto Accept switch (§10.2 <c>set_auto_accept</c>).</summary>
    /// <param name="on">Whether answers become lines with no person involved.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The switch as now set.</returns>
    /// <remarks>
    /// Governs answers that arrive afterwards and nothing else: an answer already waiting stays waiting, because
    /// turning the switch on is a statement about what should happen next, not a decision about answers a person has
    /// not looked at.
    /// </remarks>
    public Task<bool> SetAutoAcceptAsync(bool on, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        Questions.SetAutoAcceptAsync(on, nowUtc, cancellationToken: cancellationToken);

    /// <summary>What a person reviews ownership from (§10.2 <c>facilitation</c>).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The switch, and each agent's latest question.</returns>
    public async Task<FacilitationReport> ReportAsync(CancellationToken cancellationToken = default)
    {
        var questions = Questions;

        return new FacilitationReport(
            await questions.AutoAcceptAsync(cancellationToken: cancellationToken).ConfigureAwait(false),
            await questions.LatestAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Has Parley ask an agent what it owns, as a new question a person chose to spend (§10.2 <c>ask_again</c>).</summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="agent">Who to ask.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was asked, and why not when it was not.</returns>
    /// <remarks>
    /// <para>
    /// <b>No seat, because Parley sends it.</b> The ask is the facilitator's own, so a person needs to hold
    /// nothing to have it asked.
    /// </para>
    /// <para>
    /// Refused for the facilitator, a seat and a retired agent, and for an agent with a question still being decided —
    /// asking again then would lose an answer nobody has looked at. An agent with an accepted line may be asked again:
    /// that is what "ask again" is for.
    /// </para>
    /// </remarks>
    public async Task<FacilitationAction> AskAgainAsync(
        AgentName facilitator,
        AgentName agent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var agents = new AgentRepository(_connection);

        var adapter = await agents.AdapterOfAsync(agent, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (adapter is null)
        {
            return FacilitationAction.Not($"nothing has registered under '{agent.Value}', so there is nothing to ask.");
        }

        if (string.Equals(adapter, Facilitation.Adapter, StringComparison.Ordinal))
        {
            return FacilitationAction.Not("Parley does not ask itself.");
        }

        if (string.Equals(adapter, PersonSeat.Adapter, StringComparison.Ordinal))
        {
            return FacilitationAction.Not(
                $"'{agent.Value}' is a person's seat, and a person owns no repository to describe.");
        }

        if (await agents.IsRetiredAsync(agent, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return FacilitationAction.Not($"'{agent.Value}' is retired. Reinstate it first.");
        }

        var asked = await AskAsync(facilitator, agent, nowUtc, cancellationToken).ConfigureAwait(false);

        return asked.Asked ? FacilitationAction.Did : FacilitationAction.Not(asked.Problem!);
    }

    /// <summary>
    /// Sends a person's reply to an agent that asked Parley something back (§10.2 <c>reply_to_asked_back</c>).
    /// </summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="agent">The agent that asked back.</param>
    /// <param name="text">The person's words.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was sent, and why not when it was not.</returns>
    /// <remarks>
    /// <para>
    /// <b>Sent on the facilitator's exchange, because only the requester may answer a Clarify there</b> (§5.2), and
    /// <b>attributed <c>parley:person</c>, never <c>parley:facilitator</c></b>. A log whose worth is that it says
    /// who said what stops having any the moment a person's words appear under another name.
    /// </para>
    /// <para>
    /// The same bound as any message (rule 8.4), because it is one; and the same transition an agent's Clarify takes,
    /// through the exchange's own state machine, so the turn passes back to the agent and nothing else moves.
    /// </para>
    /// </remarks>
    public async Task<FacilitationAction> ReplyAsync(
        AgentName facilitator,
        AgentName agent,
        string text,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var words = (text ?? string.Empty).Trim();

        if (words.Length == 0)
        {
            return FacilitationAction.Not("an empty reply answers nothing. Nothing was sent.");
        }

        if (words.Length > MessageLimits.MaxPayloadCharacters)
        {
            return FacilitationAction.Not(
                $"a reply may be at most {MessageLimits.MaxPayloadCharacters} characters, like any message, and this " +
                $"one is {words.Length}. Nothing was sent.");
        }

        using var scope = WriteScope.Begin(_connection, ambient: null);
        var questions = Questions;

        var nothingAsked = FacilitationAction.Not(
            $"'{agent.Value}' has not asked Parley anything back, so there is nothing to reply to.");

        if (await questions.LiveAsync(agent, scope.Transaction, cancellationToken).ConfigureAwait(false)
            is not { State: QuestionState.Asked or QuestionState.AskedBack } question)
        {
            scope.Complete();
            return nothingAsked;
        }

        var exchanges = new ExchangeRepository(_connection);

        // **Whose turn it is is read from the exchange, not from the question's recorded state**, which only the sweep
        // moves. A person replying between an agent's Clarify and the next pass would otherwise be refused for asking
        // too soon -- the exchange already says the next move is Parley's, and that is the fact that decides.
        if (await exchanges.GetAsync(question.Exchange, scope.Transaction, cancellationToken).ConfigureAwait(false)
            is not { State: ExchangeState.Open } exchange
            || exchange.OwedBy != facilitator)
        {
            scope.Complete();
            return nothingAsked;
        }

        var moved = exchange.Clarify(facilitator, nowUtc);

        if (!moved.Succeeded)
        {
            scope.Complete();
            return FacilitationAction.Not($"the exchange would not take a reply: {moved.Error}.");
        }

        var payload = new System.Text.Json.Nodes.JsonObject { ["text"] = words }.ToJsonString();

        await new MessageRepository(_connection).AppendAsync(
            question.Exchange, MessageType.Clarify, facilitator, agent, Facilitation.PersonAttribution, payload, nowUtc,
            scope.Transaction, cancellationToken).ConfigureAwait(false);

        _ = await exchanges.SaveAsync(exchange, scope.Transaction, cancellationToken).ConfigureAwait(false);

        var open = (await questions.OpenAsync(scope.Transaction, cancellationToken).ConfigureAwait(false))
            .Single(q => q.Agent == agent);

        await questions.MoveAsync(open.Id, QuestionState.Asked, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return FacilitationAction.Did;
    }

    /// <summary>Discards an answer that is waiting for a person (§10.2 <c>discard_answer</c>).</summary>
    /// <param name="facilitator">The facilitator's name in this store.</param>
    /// <param name="agent">Whose answer.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was discarded, and why not when it was not.</returns>
    /// <remarks>
    /// Nothing is written and the agent keeps whatever line it had. It is not asked again unless a person asks: a
    /// discard is a decision, and the sweep asking again on its next pass would overrule it with nobody choosing to spend
    /// the agent's turn.
    /// </remarks>
    public async Task<FacilitationAction> DiscardAsync(
        AgentName facilitator,
        AgentName agent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        using var scope = WriteScope.Begin(_connection, ambient: null);
        var questions = Questions;

        if (await questions.LiveAsync(agent, scope.Transaction, cancellationToken).ConfigureAwait(false)
            is not { State: QuestionState.Waiting })
        {
            scope.Complete();
            return FacilitationAction.Not($"no answer from '{agent.Value}' is waiting, so there is nothing to discard.");
        }

        var conversation = await questions.DiscardAsync(agent, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        await AgreeDoneAsync(facilitator, conversation!, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        scope.Complete();
        return FacilitationAction.Did;
    }

    /// <summary>
    /// Agents whose first line was just accepted automatically and not yet refreshed, with the question each is for.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The questions and agents.</returns>
    public Task<IReadOnlyList<(long Question, AgentName Agent)>> RefreshDueAsync(
        CancellationToken cancellationToken = default) =>
        Questions.RefreshDueAsync(cancellationToken: cancellationToken);

    /// <summary>Records that an agent's node was asked to refresh it, so it never is again.</summary>
    /// <param name="question">The question the refresh was for.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was recorded.</returns>
    public Task<bool> RefreshSentAsync(long question, CancellationToken cancellationToken = default) =>
        Questions.RefreshSentAsync(question, cancellationToken: cancellationToken);

    /// <summary>
    /// Proposes closing the facilitator's own conversation, votes that it is done, and closes it if nobody holds work in
    /// it (SPEC.md §5.0, principle 2.8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Needed because a conversation ends by consensus of its participants</b>, and a participant
    /// that never agreed would hold every ownership conversation open for ever.
    /// </para>
    /// <para>
    /// <b>Called only once the question is settled, and it costs the agent nothing.</b> A poll asks the participants that
    /// hold open work, and by now the exchange has closed on the agent's Result — so nobody is polled, no turn is spent,
    /// and the conversation closes on the facilitator's own proposal. Only its own conversation: the identifier comes
    /// from its own question's row, never from a caller.
    /// </para>
    /// </remarks>
    private async Task AgreeDoneAsync(
        AgentName facilitator,
        string conversation,
        DateTimeOffset nowUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var conversations = new ConversationRepository(_connection);

        if (await conversations.GetAsync(conversation, transaction, cancellationToken).ConfigureAwait(false)
            is not { State: not ConversationState.Closed })
        {
            return;
        }

        // **Never while the agent still holds work in it**, which is the case of a person writing a line before the agent
        // answered. A proposal then would poll the agent, and a poll is a turn it did not ask to spend. Left open, the
        // conversation ends by the ordinary route once the agent answers or a person closes it.
        if ((await new ExchangeRepository(_connection)
                .GetOpenInConversationAsync(conversation, transaction, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return;
        }

        await conversations.BeginPollAsync(conversation, nowUtc, transaction, cancellationToken).ConfigureAwait(false);
        _ = await conversations.RecordVoteAsync(conversation, facilitator, Vote.Done, nowUtc, transaction, cancellationToken)
            .ConfigureAwait(false);
        _ = await conversations.TryConcludeAsync(conversation, nowUtc, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A conversation identifier no conversation holds or held, derived from the agent and the time.</summary>
    /// <remarks>
    /// Two agents of one role on two machines, asked in the same second, derive the same identifier, and the sweep asks
    /// everybody due in one pass — so a taken identifier is numbered rather than shared. Two agents' questions in one
    /// conversation would make each a participant in the other's.
    /// </remarks>
    private async Task<string> FreeConversationAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var candidate = Facilitation.ConversationFor(agent, nowUtc);

        for (var n = 2; await Questions.ConversationTakenAsync(candidate, transaction, cancellationToken)
                 .ConfigureAwait(false); n++)
        {
            candidate = Facilitation.Numbered(Facilitation.ConversationFor(agent, nowUtc), n);
        }

        return candidate;
    }
}

/// <summary>Whether the facilitator asked, and why not when it did not.</summary>
/// <param name="Asked">Whether a question was opened.</param>
/// <param name="Conversation">Its conversation, when asked.</param>
/// <param name="Exchange">Its Request's exchange, when asked.</param>
/// <param name="Problem">Why not, in words a person reads, when not.</param>
public sealed record AskOutcome(bool Asked, string? Conversation, long? Exchange, string? Problem)
{
    /// <summary>A question opened.</summary>
    /// <param name="conversation">Its conversation.</param>
    /// <param name="exchange">Its exchange.</param>
    /// <returns>The outcome.</returns>
    public static AskOutcome Done(string conversation, long exchange) => new(true, conversation, exchange, null);

    /// <summary>A question not opened.</summary>
    /// <param name="problem">Why.</param>
    /// <returns>The outcome.</returns>
    public static AskOutcome Not(string problem) => new(false, null, null, problem);
}

/// <summary>An agent's answer to the facilitator, recorded and waiting on a decision.</summary>
/// <param name="Question">The question's row.</param>
/// <param name="Agent">Who answered.</param>
/// <param name="Conversation">The conversation it was asked in.</param>
/// <param name="Answer">What the agent wrote, whole.</param>
/// <param name="Line">The same words on one line (<see cref="OwnershipStatement.Flatten"/>).</param>
/// <param name="Blocked">Whether the agent reported itself blocked rather than answering.</param>
public sealed record FacilitatorAnswer(
    long Question,
    AgentName Agent,
    string Conversation,
    string Answer,
    string Line,
    bool Blocked);
