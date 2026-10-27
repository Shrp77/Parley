using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// The agent operations of SPEC.md §10.1, and what happens when an agent attaches and on every tick.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose. The repositories hold the rules that need storage and <see cref="Exchange"/> holds the
/// state machine; this decides when each runs and makes every operation that moves an exchange and writes
/// a message commit both or neither (§10.1). A broker that grew its own persistence or state machine
/// would be a third place for the rules to live.
/// </para>
/// <para>
/// It owns no timer. Scheduling belongs to the host, and a broker that owned its own clock could not be
/// tested without one.
/// </para>
/// <para>
/// <b>Not thread-safe, and the host must serialise access.</b> A broker holds one connection for its
/// lifetime, and a <see cref="SqliteConnection"/> is not thread-safe. A host runs its brokers behind one
/// gate, which at this scale costs nothing — the work is a handful of indexed queries against a local file.
/// </para>
/// </remarks>
public sealed class Broker
{
    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly ConversationRepository _conversations;
    private readonly Pruner _pruner;
    private readonly BrokerOptions _options;

    /// <summary>When this broker last applied the retention policy, or <see langword="null"/> since starting.</summary>
    /// <remarks>
    /// Per process rather than stored. Losing it means a restarted broker prunes once on its first tick,
    /// which it should do anyway.
    /// </remarks>
    private DateTimeOffset? _prunedUtc;

    /// <summary>Creates a broker over an open connection.</summary>
    /// <param name="connection">
    /// An open, already-migrated connection. Borrowed and never closed: the caller opens it, keeps it,
    /// and closes it.
    /// </param>
    /// <param name="options">Lifecycle configuration, or <see langword="null"/> for defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public Broker(SqliteConnection connection, BrokerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _options = options ?? BrokerOptions.Default;
        _options.Validate();

        _connection = connection;
        _agents = new AgentRepository(connection);
        _exchanges = new ExchangeRepository(connection);
        _messages = new MessageRepository(connection);
        _conversations = new ConversationRepository(connection);
        _pruner = new Pruner(connection);
    }

    /// <summary>Gets when this broker started, or <see langword="null"/> before it has.</summary>
    /// <remarks>Set by <see cref="StartAsync"/> for an agent's broker and by <see cref="ServeFrom"/> for the host's.</remarks>
    public DateTimeOffset? StartedUtc { get; private set; }

    /// <summary>Gets the agent this broker attached for, or <see langword="null"/> before it has.</summary>
    /// <remarks>
    /// Read by <see cref="DetachAsync"/>, so a process cannot mark somebody else's agent offline. It
    /// authorises nothing: every operation still takes the acting participant explicitly, because the
    /// identity boundary is the process and it lives in the adapter.
    /// </remarks>
    public AgentName? Attached { get; private set; }

    /// <summary>The namespace §11.3 reserves for the broker's own attributions.</summary>
    public const string ReservedResponderPrefix = Responder.ReservedPrefix;

    /// <summary>The most roadmap notices one collection hands over; the rest wait for the next.</summary>
    private const int RoadmapNoticesPerCollection = 50;

    /// <summary>
    /// About how many characters of messages one collection hands over; what would pass it waits for the next.
    /// </summary>
    /// <remarks>
    /// Messages are stamped delivered as they are handed over, so a collection too large to arrive loses every one of
    /// them: the reply is refused at the adapter's message size and nothing hands them over again. One agent sending
    /// another a few hundred long Requests made that happen to everybody else's messages. Kept well under what an
    /// adapter's own output carries to a model, and never fewer than one message, so a collection always moves.
    /// </remarks>
    private const int MessageCharactersPerCollection = 64_000;

    /// <summary>The most closure polls one collection lists; the rest are listed by later ones.</summary>
    /// <remarks>
    /// One agent can open any number of conversations with another and propose each is finished. Unbounded, the polls
    /// alone made a collection too large to arrive, after its messages had been stamped delivered.
    /// </remarks>
    private const int ClosurePollsPerCollection = 200;

    /// <summary>The longest responder identity storage accepts, per §11.3.</summary>
    private const int MaxResponderLength = 64;

    /// <summary>Marks a broker the host owns as started, for the sweep.</summary>
    /// <param name="nowUtc">The instant the host began serving.</param>
    /// <exception cref="InvalidOperationException">This broker has already started.</exception>
    /// <remarks>
    /// The sweep is the broker process's own work rather than any agent's, so it runs on a broker that
    /// never attaches. This marks it started without marking anything live. Refuses a second call,
    /// because it is called once from a constructor path and a second call is a defect.
    /// </remarks>
    public void ServeFrom(DateTimeOffset nowUtc)
    {
        if (StartedUtc is not null)
        {
            throw new InvalidOperationException("This broker has already started.");
        }

        StartedUtc = nowUtc;
    }

    private static bool IsResponderCharacter(char candidate) =>
        char.IsAsciiLetterOrDigit(candidate) || candidate is '.' or '_' or ':' or '/' or '-';

    /// <summary>Throws if an adapter supplied a responder identity it may not.</summary>
    /// <param name="responderModel">The identity the adapter supplied.</param>
    /// <exception cref="ArgumentException">It is empty, malformed, or claims the <c>parley:</c> namespace.</exception>
    /// <remarks>
    /// <para>
    /// §11.3 makes refusing the reserved namespace a MUST and names this boundary as the place, because
    /// nowhere else can: every row is written by the broker, including the rows that legitimately say
    /// <c>parley:broker</c>, so no <c>CHECK</c> can tell the broker's own name from one an adapter handed
    /// it. A forged attribution would make work abandoned by an agent read as ended by a person.
    /// </para>
    /// <para>
    /// The character set is checked here as well as in the schema, so an agent cannot choose the SQL
    /// error its adapter is shown.
    /// </para>
    /// </remarks>
    private static void RequireAdapterResponder(string responderModel)
    {
        ArgumentException.ThrowIfNullOrEmpty(responderModel);

        if (responderModel.Length > MaxResponderLength || responderModel.Any(c => !IsResponderCharacter(c)))
        {
            throw new ArgumentException(
                "A responder identity is at most 64 characters of letters, digits, and the punctuation " +
                $"'.', '_', ':', '/' and '-'. See SPEC.md §11.3. Got '{responderModel}'.",
                nameof(responderModel));
        }

        if (Responder.IsReserved(responderModel))
        {
            throw new ArgumentException(
                $"The '{ReservedResponderPrefix}' responder namespace is reserved for the broker's own " +
                "attributions and an adapter may not claim it. See SPEC.md §11.3.",
                nameof(responderModel));
        }
    }

    /// <summary>Throws unless the acting participant is registered.</summary>
    /// <param name="agent">The participant the adapter claims to be.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="InvalidOperationException">No such participant is registered.</exception>
    /// <remarks>
    /// A host error rather than a refusal: the adapter was configured with a name the registry does not
    /// hold, and nothing the agent does will change that.
    /// </remarks>
    private async Task RequireRegisteredAsync(AgentName agent, CancellationToken cancellationToken)
    {
        if (!await _agents.IsRegisteredAsync(agent, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"No agent named '{agent}' is registered. An adapter must be configured with a name the " +
                "registry holds.");
        }
    }

    /// <summary>Throws unless this broker has started.</summary>
    /// <param name="operation">The operation being refused, named in the message.</param>
    /// <exception cref="InvalidOperationException"><see cref="StartAsync"/> has not been called.</exception>
    /// <remarks>
    /// Thrown rather than returned as a refusal: it describes the host's own sequencing error, which no
    /// agent can act on.
    /// </remarks>
    private void RequireStarted(string operation)
    {
        if (StartedUtc is null)
        {
            throw new InvalidOperationException(
                $"The broker has not started. Call StartAsync before {operation}.");
        }
    }

    /// <summary>Sends a Request to one participant (§5.1).</summary>
    /// <param name="from">The requester.</param>
    /// <param name="to">The receiver.</param>
    /// <param name="responderModel">What produced the Request (§11.3).</param>
    /// <param name="payloadJson">
    /// The Request document: the ask (§5.1). Storage refuses one that carries none (§9.3).
    /// </param>
    /// <param name="nowUtc">The instant of the Request.</param>
    /// <param name="conversationId">
    /// The conversation the sender names, or <see langword="null"/>. Whether it may name one, and which,
    /// depends on what it holds (§5.0).
    /// </param>
    /// <param name="cancellationToken">Cancels the Request.</param>
    /// <returns>The exchange opened and the Request as stored, or the refusal.</returns>
    /// <exception cref="InvalidOperationException">The broker has not started, or the sender is not registered.</exception>
    /// <exception cref="ArgumentException">The responder identity or payload is unusable.</exception>
    /// <exception cref="SqliteException">The payload violates a storage constraint. Nothing is written.</exception>
    /// <remarks>
    /// <para>
    /// <b>The broker assigns the conversation</b>, inside the same transaction that admits the exchange
    /// and writes the Request, so the three cannot disagree and a refused Request writes nothing.
    /// </para>
    /// <para>
    /// The policy comes from <see cref="BrokerOptions.ExchangePolicy"/> and there is no parameter for it:
    /// a control the controlled party selects is not a control.
    /// </para>
    /// </remarks>
    public async Task<RequestOutcome> RequestAsync(
        AgentName from,
        AgentName to,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("sending a request");
        RequireAdapterResponder(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);
        await RequireRegisteredAsync(from, cancellationToken).ConfigureAwait(false);

        using var scope = WriteScope.Begin(_connection, ambient: null);

        var assignment = await AssignAsync(from, conversationId, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (assignment.Refusal is { } refused)
        {
            scope.Complete();
            return RequestOutcome.Refused(refused);
        }

        var outcome = await OpenAsync(
            from, to, responderModel, payloadJson, nowUtc, assignment.ConversationId!, requestGroup: null,
            scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return outcome;
    }

    /// <summary>Sends a Request to every participant registered under a role (§5.5).</summary>
    /// <param name="from">The requester.</param>
    /// <param name="role">The role, with no machine part.</param>
    /// <param name="responderModel">What produced the Request.</param>
    /// <param name="payloadJson">The Request document.</param>
    /// <param name="nowUtc">The instant of the Request.</param>
    /// <param name="conversationId">The conversation the sender names, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the Request.</param>
    /// <returns>One outcome per participant under the role, in name order; or a single refusal.</returns>
    /// <remarks>
    /// <para>
    /// <b>One ordinary exchange per participant</b>, sharing a group identifier so the requester can tell
    /// which Results belong together. Not a plural exchange: each is admitted, clarified and resulted
    /// exactly as it would have been on its own.
    /// </para>
    /// <para>
    /// <b>The conversation is assigned once</b>, before any exchange opens, so every one lands in the same
    /// conversation — including one the broker names for a sender that named none.
    /// </para>
    /// <para>
    /// A refusal for one member does not stop the others, and a member on a machine that is off is
    /// included, so the missing part of the work is flagged rather than silently absent. There is no cap on
    /// the group's size: §17 question 12 says to measure before setting one.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<RequestOutcome>> RequestRoleAsync(
        AgentName from,
        string role,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("sending a request");
        RequireAdapterResponder(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);
        await RequireRegisteredAsync(from, cancellationToken).ConfigureAwait(false);

        var members = (await _agents.ListRoleAsync(role, cancellationToken).ConfigureAwait(false))
            .Where(a => a != from)
            .ToArray();

        if (members.Length == 0)
        {
            // A refusal rather than an empty group: "asked nobody, all done" reads as success.
            return [RequestOutcome.Refused(OpenOutcome.Refused(RequestRejection.NoSuchRole))];
        }

        ConversationAssignment assignment;

        using (var scope = WriteScope.Begin(_connection, ambient: null))
        {
            assignment = await AssignAsync(from, conversationId, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);

            scope.Complete();
        }

        if (assignment.Refusal is { } refused)
        {
            return [RequestOutcome.Refused(refused)];
        }

        // Opaque and generated here, so a requester cannot bind together exchanges that were never one Request.
        var group = Guid.NewGuid().ToString("n")[..16];
        var outcomes = new List<RequestOutcome>(members.Length);

        foreach (var member in members)
        {
            using var scope = WriteScope.Begin(_connection, ambient: null);

            outcomes.Add(await OpenAsync(
                from, member, responderModel, payloadJson, nowUtc, assignment.ConversationId!, group,
                scope.Transaction, cancellationToken).ConfigureAwait(false));

            scope.Complete();
        }

        return outcomes;
    }

    private async Task<ConversationAssignment> AssignAsync(
        AgentName from, string? conversationId, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var adapter = await _agents.AdapterOfAsync(from, transaction, cancellationToken).ConfigureAwait(false);

        return await _exchanges.AssignConversationAsync(
            from,
            conversationId,
            senderIsPerson: string.Equals(adapter, PersonSeat.Adapter, StringComparison.Ordinal),
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RequestOutcome> OpenAsync(
        AgentName from,
        AgentName to,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        string conversationId,
        string? requestGroup,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var admission = await _exchanges.TryOpenAsync(
            from, to, nowUtc, MessageIdentity.ComputeRequest(from, to, payloadJson), conversationId,
            _options.ExchangePolicy, requestGroup, transaction, cancellationToken).ConfigureAwait(false);

        if (!admission.Succeeded)
        {
            return RequestOutcome.Refused(admission);
        }

        // A Request cannot be suppressed as a duplicate here: its identity includes the exchange just
        // created. A repeated Request is refused a step earlier, by distinctness.
        var appended = await _messages.AppendAsync(
            admission.Exchange!.Id, MessageType.Request, from, to, responderModel, payloadJson, nowUtc,
            transaction, cancellationToken).ConfigureAwait(false);

        return RequestOutcome.Opened(admission, appended.Message, requestGroup);
    }

    /// <summary>Asks for or supplies something within an open exchange (§5.2).</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="from">The participant speaking. Must hold the turn.</param>
    /// <param name="responderModel">What produced it (§11.3).</param>
    /// <param name="payloadJson">The clarification document.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the clarification.</param>
    /// <param name="settles">Exchanges the person's Request settled, on a reply to one (SPEC.md §5.6); empty otherwise.</param>
    /// <returns>The message as stored, or why the exchange refused it.</returns>
    /// <remarks>
    /// One operation for both directions, because which it is depends only on who is speaking. A
    /// clarification from the receiver counts against any cap set; the requester's reply does not.
    /// </remarks>
    public Task<SayOutcome> ClarifyAsync(
        long exchangeId,
        AgentName from,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default,
        IReadOnlyList<long>? settles = null) =>
        SayAsync(
            exchangeId, from, MessageType.Clarify, responderModel, payloadJson, nowUtc,
            (exchange, speaker) => exchange.Clarify(speaker, nowUtc),
            settles ?? [], cancellationToken);

    /// <summary>Sends the Result of a Request, closing its exchange (§5.3).</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="from">The receiver, holding the turn.</param>
    /// <param name="responderModel">What produced it (§11.3).</param>
    /// <param name="payloadJson">The Result document: one outcome for the ask as a whole.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the Result.</param>
    /// <param name="settles">Exchanges the person's Request settled, on a reply to one (SPEC.md §5.6); empty otherwise.</param>
    /// <returns>The Result as stored, or why the exchange refused it.</returns>
    /// <exception cref="ArgumentException">The payload has no readable outcomes.</exception>
    /// <remarks>
    /// <para>
    /// <b>The rolled-up outcome is derived here, not supplied.</b> A sender reporting every item blocked
    /// under an exchange recorded complete would make §15 describe a deployment that does not exist.
    /// </para>
    /// <para>
    /// A Request is answered whole: the Result carries one outcome for the ask, recorded under
    /// <see cref="PayloadShape.WholeRequest"/> (SPEC.md §5.1, §5.3).
    /// </para>
    /// </remarks>
    public async Task<SayOutcome> ResultAsync(
        long exchangeId,
        AgentName from,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default,
        IReadOnlyList<long>? settles = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        if (!PayloadShape.TryReadOutcomes(payloadJson, out var outcomes))
        {
            throw new ArgumentException(
                "A Result carries an 'outcomes' array, and every entry names an item id and one of done, " +
                "answered, acknowledged or blocked. See SPEC.md §5.3.",
                nameof(payloadJson));
        }

        var rollup = OutcomeTokens.RollUp([.. outcomes.Select(o => o.Outcome)]);

        return await SayAsync(
            exchangeId, from, MessageType.Result, responderModel, payloadJson, nowUtc,
            (exchange, speaker) => exchange.Close(speaker, nowUtc, rollup),
            settles ?? [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a transition and writes the message announcing it, as one act.</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="from">The participant speaking.</param>
    /// <param name="type">The message kind.</param>
    /// <param name="responderModel">What produced it.</param>
    /// <param name="payloadJson">The document.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="transition">The domain transition to attempt.</param>
    /// <param name="settles">Exchanges the person's Request settled (SPEC.md §5.6).</param>
    /// <param name="cancellationToken">Cancels the move.</param>
    /// <returns>The message as stored, or why the move was refused.</returns>
    /// <remarks>
    /// <para>
    /// The read is inside the transaction, so the decision and the write see the same row. The recipient
    /// is derived rather than passed, so a message cannot be addressed to somebody outside the exchange,
    /// and the conversation is the exchange's own (§5.0).
    /// </para>
    /// <para>
    /// A duplicate inside the deduplication window is a transport retry of a move already applied, so it
    /// is reported as sent and the transition is not applied twice.
    /// </para>
    /// <para>
    /// <b>What the reply settles is applied after the message is written, inside the same transaction.</b> A retry is
    /// recognised at the write, before anything is settled, so it is reported as sent rather than refused for naming
    /// questions its first copy already closed. And a name that cannot be settled refuses the whole reply by leaving the
    /// transaction uncommitted: the message, the transition and every settle go together or not at all.
    /// </para>
    /// </remarks>
    private async Task<SayOutcome> SayAsync(
        long exchangeId,
        AgentName from,
        MessageType type,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        Func<Exchange, AgentName, TransitionResult> transition,
        IReadOnlyList<long> settles,
        CancellationToken cancellationToken)
    {
        RequireStarted(type == MessageType.Result ? "sending a result" : "clarifying");
        RequireAdapterResponder(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        using var scope = WriteScope.Begin(_connection, ambient: null);

        var exchange = await _exchanges.GetAsync(exchangeId, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (exchange is null)
        {
            // "Not open" rather than "no such exchange", so identifiers cannot be probed for.
            return SayOutcome.Refused(TransitionError.ExchangeNotOpen);
        }

        var moved = transition(exchange, from);

        if (!moved.Succeeded)
        {
            return SayOutcome.Refused(moved.Error);
        }

        var to = from == exchange.Requester ? exchange.Receiver : exchange.Requester;

        var appended = await _messages.AppendAsync(
            exchangeId, type, from, to, responderModel, payloadJson, nowUtc, scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        if (!appended.Stored)
        {
            return SayOutcome.Said(appended.Message, stored: false);
        }

        if (!await _exchanges.SaveAsync(exchange, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            // Unreachable: the read happened inside this transaction, which holds the write lock.
            throw new InvalidOperationException(
                $"Exchange {exchangeId} changed under a write lock held for this operation. This indicates " +
                "the connection is not honouring IMMEDIATE transactions.");
        }

        if (settles.Count == 0)
        {
            scope.Complete();
            return SayOutcome.Said(appended.Message, appended.Stored);
        }

        var settled = await SettleAsync(exchange, from, settles, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (settled.Problems.Count > 0)
        {
            // Not completed, so the message and the transition go with it: the whole reply is refused (§5.6).
            return SayOutcome.RefusedToSettle(settled.Problems);
        }

        scope.Complete();
        return SayOutcome.Said(appended.Message, settled.Settled, settled.HandedBack);
    }

    /// <summary>Settles what a reply on the person's Request names (SPEC.md §5.6), or says why it cannot.</summary>
    /// <param name="reply">The exchange the reply is on, as it stood before the reply moved it.</param>
    /// <param name="from">The agent replying.</param>
    /// <param name="settles">What it named.</param>
    /// <param name="nowUtc">The reply's instant, which every settle carries so a surface can show them beside it.</param>
    /// <param name="transaction">The reply's own transaction.</param>
    /// <param name="cancellationToken">Cancels the reads and writes.</param>
    /// <returns>What was settled and handed back, or every problem found; when there are problems nothing is kept.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every name is checked before any is refused</b>, so the refusal lists them all and the agent can send again
    /// without them in one move rather than discovering them one at a time.
    /// </para>
    /// <para>
    /// The order of the checks is the order of the problems' meaning: an exchange that does not exist has no conversation
    /// to compare, and one in another conversation is reported as that whether or not it is open. Storage checks the
    /// conversation, the pairing and the person again when the outcome is written.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<long> Settled, IReadOnlyList<long> HandedBack,
        IReadOnlyList<(long Exchange, SettleProblem Problem)> Problems)> SettleAsync(
        Exchange reply,
        AgentName from,
        IReadOnlyList<long> settles,
        DateTimeOffset nowUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var named = settles.Distinct().ToList();

        if (named.Count > Settling.MostNamed)
        {
            throw new ArgumentException(
                $"A reply may settle at most {Settling.MostNamed} exchanges; this one named {named.Count}.",
                nameof(settles));
        }

        var person = reply.Requester;
        var problems = new List<(long Exchange, SettleProblem Problem)>();

        // Only a reply on a Request the person opened with this agent can settle anything.
        if (!PersonSeat.Is(person) || reply.Receiver != from)
        {
            return ([], [], [.. named.Select(id => (id, SettleProblem.NotFromThePerson))]);
        }

        var conversation = (await _messages.GetRequestAsync(reply.Id, transaction, cancellationToken)
            .ConfigureAwait(false))?.ConversationId;

        var settled = new List<long>();
        var handedBack = new List<long>();

        foreach (var id in named)
        {
            // The reply's own exchange: the person did not owe the move on it when the agent replied.
            if (id == reply.Id)
            {
                problems.Add((id, SettleProblem.NotTheirMove));
                continue;
            }

            var target = await _exchanges.GetAsync(id, transaction, cancellationToken).ConfigureAwait(false);
            var targetConversation = target is null
                ? null
                : (await _messages.GetRequestAsync(id, transaction, cancellationToken).ConfigureAwait(false))
                    ?.ConversationId;

            if (target is null)
            {
                problems.Add((id, SettleProblem.NotOpen));
            }
            else if (!string.Equals(targetConversation, conversation, StringComparison.Ordinal))
            {
                problems.Add((id, SettleProblem.OtherConversation));
            }
            else if (target.State != ExchangeState.Open)
            {
                problems.Add((id, SettleProblem.NotOpen));
            }
            else if (!target.Involves(from) || !target.Involves(person))
            {
                problems.Add((id, SettleProblem.NotBetweenYou));
            }
            else if (target.OwedBy != person)
            {
                problems.Add((id, SettleProblem.NotTheirMove));
            }
            else
            {
                var moved = target.Requester == from ? target.Settle(from, reply.Id, nowUtc) : target.TurnBack(from);

                if (!moved.Succeeded)
                {
                    // Unreachable after the checks above, which are the transitions' own conditions.
                    problems.Add((id, SettleProblem.NotTheirMove));
                    continue;
                }

                if (!await _exchanges.SaveAsync(target, transaction, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Exchange {id} changed under a write lock held for this operation.");
                }

                (target.Requester == from ? settled : handedBack).Add(id);
            }
        }

        return problems.Count > 0 ? ([], [], problems) : (settled, handedBack, []);
    }

    /// <summary>Lists the participants this one may address (§10.1's <c>agents()</c>).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every registered participant a person has not retired.</returns>
    /// <remarks>
    /// A projection rather than the stored record: the workspace, session handle and adapter configuration
    /// may not cross to another participant.
    /// </remarks>
    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        RequireStarted("listing agents");
        return await _agents.ListRoutableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts a roadmap forward for a person to confirm (§5.7).</summary>
    /// <param name="proposer">The attached caller, which is the only way a proposer is ever named.</param>
    /// <param name="content">The title and the items.</param>
    /// <param name="conversationId">Where it came from, or <see langword="null"/>.</param>
    /// <param name="revises">The title of a roadmap it would change, a hint, or <see langword="null"/>.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The proposal's identifier, or a sentence saying why it was refused.</returns>
    /// <exception cref="InvalidOperationException">The broker has not started, or the proposer is not registered.</exception>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of what an agent can do with a roadmap</b> (principle 2.12). It stores a proposal and
    /// nothing else: no exchange, no message, no roadmap. There is no operation here, or reachable from here, by
    /// which an agent reads, changes, reorders, removes from or marks done a roadmap, and none may be added.
    /// </para>
    /// <para>
    /// The person seat and Parley are refused with a sentence here, before storage's trigger would refuse them
    /// with a constraint's text: neither is an agent, and a roadmap a person confirms is not written for the person
    /// by the person (§12.3).
    /// </para>
    /// </remarks>
    public async Task<ProposalOutcome> ProposeRoadmapAsync(
        AgentName proposer,
        RoadmapContent content,
        string? conversationId,
        string? revises,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("proposing a roadmap");
        ArgumentNullException.ThrowIfNull(content);
        await RequireRegisteredAsync(proposer, cancellationToken).ConfigureAwait(false);

        if (PersonSeat.Is(proposer) || Facilitation.Is(proposer))
        {
            return new ProposalOutcome(null, "Only an agent proposes a roadmap. The person confirms one; Parley proposes none.");
        }

        var (id, problem) = await new RoadmapRepository(_connection).ProposeAsync(
            proposer, conversationId, revises, content, nowUtc, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ProposalOutcome(id, problem);
    }

    /// <summary>Says whether the caller is finished with a conversation (§5.0).</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="agent">The participant speaking.</param>
    /// <param name="finished"><see langword="true"/> to agree it is finished; <see langword="false"/> to decline.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the conversation is now closed, or a refusal.</returns>
    /// <remarks>
    /// <para>
    /// One operation for proposing and answering, because they are the same statement from the caller's
    /// side and it cannot know which situation it is in.
    /// </para>
    /// <para>
    /// Declining when nothing is being decided starts nothing: a conversation that is open already means
    /// somebody is not finished. And nothing closes a conversation for a participant's silence.
    /// </para>
    /// <para>
    /// <b>A live roadmap item's conversation is the person's to close</b> (§5.7), so agreeing to close one is refused
    /// and starts no poll. A closed conversation never reopens and an item has only the one, so participants closing
    /// it while a part was blocked would leave the item nowhere to carry on; the person instead answers the blocker
    /// with a new request in it, or closes it.
    /// </para>
    /// </remarks>
    public async Task<ClosureOutcome> ConcludeAsync(
        string conversationId,
        AgentName agent,
        bool finished,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("concluding");
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        using var scope = WriteScope.Begin(_connection, ambient: null);

        // Checked before anything else is read, so a non-participant learns nothing — not even whether the
        // conversation exists.
        if (!await _conversations.IsParticipantAsync(conversationId, agent, scope.Transaction, cancellationToken)
                .ConfigureAwait(false))
        {
            return ClosureOutcome.NotAParticipant();
        }

        var conversation = await _conversations.GetAsync(conversationId, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            return ClosureOutcome.NotAParticipant();
        }

        if (conversation.State == ConversationState.Closed)
        {
            return ClosureOutcome.Recorded(concluded: true);
        }

        if (conversation.State == ConversationState.Open && !finished)
        {
            return ClosureOutcome.Recorded(concluded: false);
        }

        if (finished && await _conversations.BelongsToALiveItemAsync(conversationId, scope.Transaction, cancellationToken)
                .ConfigureAwait(false))
        {
            return ClosureOutcome.PersonCloses();
        }

        await _conversations.BeginPollAsync(conversationId, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        await _conversations.RecordVoteAsync(
            conversationId, agent, finished ? Vote.Done : Vote.NotDone, nowUtc, scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        var concluded = await _conversations.TryConcludeAsync(
            conversationId, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return ClosureOutcome.Recorded(concluded);
    }

    /// <summary>Closes every polling conversation whose participants have all agreed.</summary>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many closed.</returns>
    /// <remarks>
    /// A poll everybody answered closes on the last vote without waiting for a tick. What closes here is a
    /// poll whose last decline lapsed because its holder was retired — nothing else would notice the
    /// registry moved.
    /// </remarks>
    private async Task<int> ConcludeSatisfiedPollsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var closed = 0;

        foreach (var conversationId in await _conversations.GetPollingAsync(cancellationToken: cancellationToken)
                     .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _conversations.TryConcludeAsync(conversationId, nowUtc, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
            {
                closed++;
            }
        }

        return closed;
    }

    /// <summary>Attaches an agent (§14).</summary>
    /// <param name="agent">The agent this broker speaks for.</param>
    /// <param name="nowUtc">The instant of the attach.</param>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>Whether it was marked live, and how much of its work it is being given again.</returns>
    /// <exception cref="InvalidOperationException">No agent by that name is registered.</exception>
    /// <remarks>
    /// <para>
    /// <b>Nothing about the work changes.</b> An exchange is open until its receiver sends a Result or its
    /// conversation closes (§6), and an agent attaching is neither. What an agent coming back is owed is
    /// the work it had collected before its last session ended: the latest message it owes a move on is
    /// marked undelivered, so its next collection hands the work over again ahead of anything new.
    /// </para>
    /// <para>
    /// <b>Nothing is given again if a session is already attached.</b> Two windows on one repository are
    /// two sessions for one agent, and the second must not re-deliver what the first is working on.
    /// </para>
    /// <para>
    /// Marking live does not disturb a retirement: a retired agent can start its server and is still
    /// refused every operation.
    /// </para>
    /// </remarks>
    public async Task<StartResult> StartAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await RequireRegisteredAsync(agent, cancellationToken).ConfigureAwait(false);

        // Read before the mark is written, because writing it destroys the answer.
        var alreadyAttached = await _agents.IsAttachedAsync(agent, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var live = await _agents.MarkLiveAsync(agent, sessionRef: null, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        var redelivered = 0;

        if (!alreadyAttached && live > 0)
        {
            foreach (var exchange in await _exchanges.GetAbandonedOwedByAsync(agent, cancellationToken)
                         .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await _messages.GetLatestOwedAsync(exchange.Id, agent, cancellationToken).ConfigureAwait(false)
                        is { } owed
                    && await _messages.RedeliverAsync(owed.Id, cancellationToken).ConfigureAwait(false))
                {
                    redelivered++;
                }
            }
        }

        Attached = agent;

        // Recorded last, so a sequence that throws part-way leaves a broker that refuses to operate.
        StartedUtc = nowUtc;

        return new StartResult(live, redelivered);
    }

    /// <summary>Records that this broker's agent is stopping.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the agent was marked offline.</returns>
    /// <exception cref="InvalidOperationException"><see cref="StartAsync"/> has not been called.</exception>
    /// <remarks>
    /// The counterpart to attaching, and the only thing it does. A disconnect records that the connection
    /// closed (§14); the agent's open exchanges stay open, and the flags of §6 say whether they move.
    /// </remarks>
    public async Task<bool> DetachAsync(CancellationToken cancellationToken = default)
    {
        RequireStarted("detaching");

        return await _agents.MarkOfflineAsync(Attached!.Value, cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>The open work an agent owes that the resume gate permits waking it for (§14).</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The exchanges; empty unless the resume policy is <see cref="ResumePolicy.Always"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>The policy decides only whether agents are woken, never whether the work survives.</b> Under
    /// <see cref="ResumePolicy.Never"/> and <see cref="ResumePolicy.Ask"/> this returns nothing and the
    /// work stays open and flagged; under <c>ask</c> the person confirms by starting the agent.
    /// </para>
    /// <para>
    /// A gate that throws is not caught: it is the host's own code, and its defect is the host's to see.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> OutstandingToWakeForAsync(
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        if (_options.ResumePolicy != ResumePolicy.Always)
        {
            return [];
        }

        var permitted = new List<Exchange>();

        foreach (var exchange in await _exchanges.GetOwedByAsync(agent, cancellationToken).ConfigureAwait(false))
        {
            if (await _options.ResumeGate.MayResumeAsync(exchange, cancellationToken).ConfigureAwait(false))
            {
                permitted.Add(exchange);
            }
        }

        return permitted;
    }

    /// <summary>Runs one maintenance pass.</summary>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass did.</returns>
    /// <exception cref="InvalidOperationException">The broker has not started.</exception>
    /// <remarks>
    /// <para>
    /// <b>There is no exchange sweep.</b> Nothing times an exchange out (principle 2.8); silence is flagged
    /// when a person asks for the flags, derived from each exchange's own thresholds, and changes nothing.
    /// </para>
    /// <para>
    /// What is left: polls whose last decline lapsed, and
    /// retention on its own schedule, last because closing a conversation makes its messages prunable.
    /// </para>
    /// </remarks>
    public async Task<TickResult> TickAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        RequireStarted("ticking");

        var concluded = await ConcludeSatisfiedPollsAsync(nowUtc, cancellationToken).ConfigureAwait(false);

        var pruned = PruneResult.Nothing;

        // A clock that moved backwards prunes rather than waiting, or one tick with a fast clock would
        // skip every sweep until real time caught up — silently, since a log that is not shrinking looks
        // like one with nothing due.
        if (_prunedUtc is not { } last || nowUtc < last || nowUtc - last >= _options.RetentionInterval)
        {
            pruned = await _pruner.PruneAsync(_options.Retention, nowUtc, cancellationToken).ConfigureAwait(false);
            _prunedUtc = nowUtc;
        }

        return new TickResult(concluded, pruned);
    }

    /// <summary>Collects what is waiting for a participant, debts first (§4.2, §7.3).</summary>
    /// <param name="agent">The participant collecting.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="limit">The most messages to return, or <see langword="null"/> for as many as one collection carries.</param>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>
    /// The messages, each already wrapped in its envelope, how many are still waiting, and any closure poll outstanding.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Wrapped here and nowhere else</b> (§13), with a fresh token per message so a sender that saw one
    /// cannot close the next one's payload block.
    /// </para>
    /// <para>
    /// <b>A Result carries its Request back</b> (§4.2), because the requester's context will have moved
    /// on and an identifier alone means nothing to a model that no longer remembers sending it.
    /// </para>
    /// <para>
    /// Collecting a Request records it on the exchange, which is where §6's second stage begins. The
    /// exchange is told before the message is stamped: a crash in between hands the Request over again,
    /// where the other order would leave it flagged as uncollected while somebody worked on it.
    /// </para>
    /// </remarks>
    public async Task<Inbox> DrainInboxAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("collecting an inbox");

        var pending = await _messages.GetPendingAsync(agent, limit, cancellationToken).ConfigureAwait(false);
        var owed = await _messages.GetOwedExchangeIdsAsync(agent, cancellationToken).ConfigureAwait(false);
        var delivered = new List<DeliveredMessage>(pending.Count);
        var handed = 0;

        // **The oldest message that is not a debt rides along** (SPEC.md §7.3). Debts come first and a collection is
        // bounded, so a steady stream of Requests held every Result, and every message on an exchange the caller does not
        // owe, behind it for good. Room is kept for that one message, so a flood delays it by one collection at most.
        var riding = pending.FirstOrDefault(m => !owed.Contains(m.ExchangeId));
        (DeliveredMessage One, int Size)? kept = riding is null ? null : await BuildAsync(riding, agent, cancellationToken).ConfigureAwait(false);
        var full = false;

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rides = ReferenceEquals(message, riding);

            if (full && !rides)
            {
                continue;
            }

            var (one, size) = rides ? kept!.Value : await BuildAsync(message, agent, cancellationToken).ConfigureAwait(false);

            // **Built and measured before it is stamped**, so a message that would make the collection too large to
            // arrive is left waiting for the next one rather than marked delivered and lost with the rest.
            var room = rides || delivered.Count == 0 || handed + size + (kept is { Size: var reserved } ? reserved : 0)
                       <= MessageCharactersPerCollection;

            if (!room)
            {
                // Past the bound: nothing more but the message riding along, which the bound kept room for.
                full = true;
                continue;
            }

            if (message.Type == MessageType.Request)
            {
                await RecordCollectionAsync(message.ExchangeId, nowUtc, cancellationToken).ConfigureAwait(false);
            }

            if (rides)
            {
                kept = null;
            }

            if (!await _messages.MarkDeliveredAsync(message.Id, nowUtc, cancellationToken).ConfigureAwait(false))
            {
                // Delivered by a drain that raced this one; skipped rather than handed over twice.
                continue;
            }

            handed += size;
            delivered.Add(one);
        }

        var waiting = await _messages.CountPendingAsync(agent, cancellationToken).ConfigureAwait(false);
        // Bounded like file notices, and nothing is lost by it: a poll stays outstanding until it is answered, so the rest
        // are listed by later collections (SPEC.md §10.1).
        var polls = await _conversations.GetOutstandingPollsAsync(agent, ClosurePollsPerCollection, cancellationToken)
            .ConfigureAwait(false);
        var pollsWaiting = polls.Count < ClosurePollsPerCollection
            ? 0
            : await _conversations.CountOutstandingPollsAsync(agent, cancellationToken).ConfigureAwait(false) - polls.Count;

        // Whatever the limit said about messages (SPEC.md §9.5): a limit meant for messages must not hold back the
        // notices, and the notices carry their own bound.
        var notices = await new FileNoticeRepository(_connection).CollectAsync(agent, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // What became of the agent's own proposals. Never a reason to wake anything (SPEC.md §5.7): collected when
        // the agent next looks, like a change notice for a file.
        var proposals = await new RoadmapRepository(_connection)
            .DrainNoticesAsync(agent, RoadmapNoticesPerCollection, nowUtc, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new Inbox(delivered, polls, notices, proposals, waiting, pollsWaiting);
    }

    /// <summary>Wraps one waiting message as it is handed over, and measures it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="agent">The participant collecting.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The message as delivered, and how many characters it carries toward a collection's bound.</returns>
    private async Task<(DeliveredMessage One, int Size)> BuildAsync(
        Message message, AgentName agent, CancellationToken cancellationToken)
    {
        // Only to the agent that sent the Request. A person closing a conversation writes a closed Result to the
        // receiver as well, and attaching the Request there would frame a peer's text as the agent's own (§13).
        var request = message.Type == MessageType.Result
                      && await _messages.GetRequestAsync(message.ExchangeId, cancellationToken: cancellationToken)
                          .ConfigureAwait(false) is { } asked
                      && asked.From == agent
            ? asked.PayloadJson
            : null;

        // **Beside a Request from the person, the agent's own open questions to them** (SPEC.md §5.6), so it can say
        // what the person's words settled without searching. Its own words, returned to it, and bounded (§13).
        IReadOnlyList<SettleableQuestion>? settleable = null;
        var leftOut = 0;

        if (message.Type == MessageType.Request && PersonSeat.Is(message.From))
        {
            var (questions, total) = await _exchanges.GetSettleableAsync(
                agent, message.From, message.ConversationId, Settling.MostListed, cancellationToken)
                .ConfigureAwait(false);

            settleable = [.. questions.Select(q => q with { Asked = Settling.Cut(q.Asked) })];
            leftOut = total - questions.Count;
        }

        // **And what the person has said since the agent asked back** (SPEC.md §5.6). A Request handed back to the
        // agent is only the person's first words; the answer to its question went in another exchange, which a model
        // whose context has moved on no longer sees. Each is wrapped like any participant's words (§13).
        IReadOnlyList<AnswerSince>? since = null;

        if (message.Type == MessageType.Request && PersonSeat.Is(message.From) && message.ConversationId is { Length: > 0 } talk)
        {
            since = await SinceAsync(agent, message, talk, cancellationToken).ConfigureAwait(false);
        }

        var one = new DeliveredMessage(
            message,
            TrustEnvelope.Wrap(
                message.From,
                message.ResponderModel,
                message.PayloadJson,
                TrustEnvelope.NewToken(),
                message.ConversationId,
                request),
            settleable,
            leftOut,
            since);

        return (one, one.Envelope.Length
                     + (one.Settleable?.Sum(q => q.Asked.Length) ?? 0)
                     + (one.Since?.Sum(a => a.Envelope.Length) ?? 0));
    }

    /// <summary>What the person said to an agent after it last spoke on the exchange a Request opened.</summary>
    /// <param name="agent">The agent the Request is delivered to.</param>
    /// <param name="request">The person's Request being delivered.</param>
    /// <param name="conversation">Its conversation.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The latest <see cref="Settling.MostSince"/>, oldest first, or null when the agent never asked back on it.</returns>
    private async Task<IReadOnlyList<AnswerSince>?> SinceAsync(
        AgentName agent, Message request, string conversation, CancellationToken cancellationToken)
    {
        var talk = await _messages.GetConversationAsync(conversation, cancellationToken).ConfigureAwait(false);

        var asked = talk.Where(m => m.ExchangeId == request.ExchangeId && m.Type == MessageType.Clarify && m.From == agent)
            .Select(m => m.Id)
            .DefaultIfEmpty(0)
            .Max();

        if (asked == 0)
        {
            return null;
        }

        var later = talk
            .Where(m => m.Id > asked
                        && m.ExchangeId != request.ExchangeId
                        && m.Type == MessageType.Request
                        && m.From == request.From
                        && m.To == agent)
            .TakeLast(Settling.MostSince)
            .Select(m => new AnswerSince(
                m.ExchangeId,
                m.CreatedUtc,
                TrustEnvelope.Wrap(m.From, m.ResponderModel, m.PayloadJson, TrustEnvelope.NewToken(), m.ConversationId)))
            .ToList();

        return later;
    }

    private async Task RecordCollectionAsync(long exchangeId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var exchange = await _exchanges.GetAsync(exchangeId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (exchange is null || exchange.DeliveredUtc is not null || !exchange.Deliver(nowUtc).Succeeded)
        {
            return;
        }

        await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
