using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// Owns the lifecycle: what happens when the broker starts, and what happens on every tick.
/// </summary>
/// <remarks>
/// <para>
/// Everything below this line was already written, tested, and unreachable. The repositories
/// know how to orphan an exchange and the reaper knows when to; nothing decided <em>when to
/// ask</em>. That is all this type is, and keeping it that thin is the point — a broker that
/// grew its own persistence or its own state machine would be a third place for the rules to
/// live.
/// </para>
/// <para>
/// It owns no timer. Scheduling belongs to the host, and a broker that owned its own clock
/// could not be tested without one — the same reason <see cref="Reaper"/> takes an instant
/// rather than reading it.
/// </para>
/// <para>
/// <b>Not thread-safe, and the host must serialise access.</b> A broker holds one connection
/// for its lifetime, which contradicts the advice on the repositories to give each unit of work
/// its own — deliberately, because the two constraints pull against each other. A
/// <see cref="SqliteConnection"/> is not thread-safe, and neither is
/// <see cref="StartedUtc"/>, so calling <see cref="TickAsync"/> from a timer while
/// <see cref="ResumeOwedByAsync"/> runs on another thread is a data race in both.
/// </para>
/// <para>
/// A per-unit-of-work broker is not the answer either: <see cref="StartedUtc"/> is
/// per-process state that the ghosting clock depends on, and an instance created for one call
/// would not have it. So the resolution is that a host runs its brokers on one thread each,
/// which at this scale costs nothing — the work is a handful of indexed queries against a
/// local file, and §4.1 already contemplates one process hosting several brokers, one per
/// project, which parallelises across projects rather than within one.
/// </para>
/// <para>
/// What would change this is a host that needs concurrency inside a single project. The shape
/// that fits is a connection factory rather than a connection, so each operation opens its own
/// and <see cref="StartedUtc"/> stays on the broker. That is a larger change than it looks,
/// because the in-memory databases the tests use are private to their connection, and it is not
/// worth making against a host that does not exist yet.
/// </para>
/// </remarks>
public sealed class Broker
{
    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly ConversationRepository _conversations;
    private readonly Reaper _reaper;
    private readonly BrokerOptions _options;

    /// <summary>SQLite's extended result code for a unique index violation.</summary>
    /// <remarks>
    /// The code says what kind of failure occurred and is stable; it does not say which index
    /// failed, and this table carries more than one. So the class of failure is matched on the
    /// code and the specific index on a column name — ours, from our own schema, rather than
    /// SQLite's prose around it. That is the part of the message that cannot drift without us
    /// changing it, and a rename that broke the match would break a test in the same commit.
    /// </remarks>
    private const int UniqueConstraintViolation = 2067;

    /// <summary>
    /// Creates a broker over an open connection.
    /// </summary>
    /// <param name="connection">
    /// An open, already-migrated connection to this project's database. The broker borrows it
    /// and never closes it: it outlives individual operations but not the host, and a type that
    /// disposed something it was handed would make the two lifetimes hard to reason about. The
    /// caller opens it, keeps it, and closes it.
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
        _reaper = new Reaper(_exchanges);
    }

    /// <summary>
    /// Gets when this broker last started, or <see langword="null"/> before it has.
    /// </summary>
    /// <remarks>
    /// Read by the ghosting clock, which runs from the later of this and the instant of
    /// orphaning. A broker that was down overnight returns to exchanges whose grace period
    /// elapsed while nothing could possibly have resumed them.
    /// </remarks>
    public DateTimeOffset? StartedUtc { get; private set; }

    /// <summary>
    /// Gets the agent this broker attached for, or <see langword="null"/> before it has.
    /// </summary>
    /// <remarks>
    /// Recorded because the attach sequence of SPEC.md §14 is scoped to one agent, so the broker
    /// knows which. <see cref="DetachAsync"/> reads it, which is what stops a process marking
    /// somebody else's agent offline — there is no parameter to pass the wrong name through.
    /// <para>
    /// It is not used to <em>authorise</em> anything. Every agent operation still takes the
    /// acting agent explicitly, because the broker is a library and the adapter above it is what
    /// binds an identity to a process; making it implicit here would move that decision somewhere
    /// the tests could not exercise several agents against one database. The identity boundary is
    /// the process, and it lives in the adapter.
    /// </para>
    /// </remarks>
    public AgentName? Attached { get; private set; }

    /// <summary>
    /// The namespace SPEC.md §11.3 reserves for the broker's own attributions.
    /// </summary>
    private const string ReservedResponderPrefix = "parley:";

    /// <summary>
    /// Throws if an adapter has claimed the reserved responder namespace.
    /// </summary>
    /// <param name="responderModel">The responder identity the adapter supplied.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="responderModel"/> is empty, or claims the <c>parley:</c> namespace.
    /// </exception>
    /// <remarks>
    /// <para>
    /// SPEC.md §11.3 makes this a MUST and names this exact boundary as the place to enforce
    /// it, because nowhere else can. Every row is written by the broker, including the rows
    /// that legitimately say <c>parley:broker</c> — a ghosting close, a forced <c>unknown</c> —
    /// so no <c>CHECK</c> can tell the broker writing its own name from the broker faithfully
    /// recording what an adapter handed it. The column sees what was said, never who was
    /// speaking. This method is the one place the distinction still exists, and it exists here
    /// only because these three operations are where adapter-supplied values enter.
    /// </para>
    /// <para>
    /// Worth something to forge, which is why it is a MUST rather than hygiene. §15 identifies
    /// an unhealthy deployment by counting broker-authored closes, so an agent that can write
    /// that attribution can hide its own failures among the infrastructure's, or manufacture
    /// evidence that a peer's model was down. A namespace anyone may claim identifies nobody,
    /// and §11.3 rests entirely on responder identity meaning something.
    /// </para>
    /// <para>
    /// Compared without case sensitivity, because the schema's character set admits
    /// <c>PARLEY:broker</c> and a reader skimming a log would not pause at it. Ordinal, because
    /// this is a protocol token and not text in anybody's language.
    /// </para>
    /// <para>
    /// Throws rather than returning a refusal, for the reason <see cref="RequireStarted"/>
    /// does: this describes a defect in the caller, not a fact about the world, and there is
    /// nothing an agent could do about it if it were told.
    /// </para>
    /// </remarks>
    private static void RequireAdapterResponder(string responderModel)
    {
        ArgumentException.ThrowIfNullOrEmpty(responderModel);

        if (responderModel.StartsWith(ReservedResponderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The '{ReservedResponderPrefix}' responder namespace is reserved for the " +
                "broker's own attributions and an adapter may not claim it. See SPEC.md §11.3.",
                nameof(responderModel));
        }
    }

    /// <summary>
    /// Throws unless the acting agent is registered.
    /// </summary>
    /// <param name="agent">The agent the adapter claims to be.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="InvalidOperationException">No such agent is registered.</exception>
    /// <remarks>
    /// <para>
    /// A host error rather than an agent one, which is why it throws where a peer being
    /// unroutable returns a refusal. An agent cannot register itself, so there is nothing for it
    /// to do about this; what it means is that the adapter was configured with a name the
    /// registry does not hold, and every operation it attempts will fail the same way.
    /// </para>
    /// <para>
    /// Added because the failure was otherwise a foreign-key violation from three layers down,
    /// arriving on whichever operation happened to be first. A deployment misconfigured this way
    /// is common — a typo, or a manifest that was never loaded — and it deserves a message that
    /// names the cause.
    /// </para>
    /// </remarks>
    private async Task RequireRegisteredAsync(AgentName agent, CancellationToken cancellationToken)
    {
        if (!await _agents.IsRegisteredAsync(agent, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"No agent named '{agent}' is registered. An adapter must be configured with a " +
                "name the registry holds; register it, or load the manifest that declares it.");
        }
    }

    /// <summary>
    /// Throws unless this broker has attached an agent.
    /// </summary>
    /// <param name="operation">The operation being refused, named in the message.</param>
    /// <exception cref="InvalidOperationException"><see cref="StartAsync"/> has not been called.</exception>
    /// <remarks>
    /// <para>
    /// Every agent-facing operation is guarded, not only the sweep, because all of them read
    /// state that SPEC.md §14's attach sequence exists to invalidate. Before it runs, this
    /// agent's debts are still open on behalf of a process that is gone and the registry still
    /// holds whatever liveness its last session left — so admission counts stale exchanges against §7.1's
    /// cap, cycle detection finds cycles among questions nobody is working on, and the
    /// reachability check reads an <c>unreachable</c> mark that a restart is precisely the
    /// occasion to clear. That last one is the worst of them: it is the one refusal that hands
    /// back no work, and its documented meaning is that nothing is coming from this peer, so
    /// stop asking.
    /// </para>
    /// <para>
    /// Throwing rather than returning a refusal, and the difference matters. Every refusal in
    /// <see cref="OpenOutcome"/> is addressed to an agent and describes the world; this is
    /// addressed to the host and describes the host's own sequencing error. An agent cannot act
    /// on it, and dressing it as a refusal would have agents reporting to their principals that
    /// a peer was unreachable when the truth is that nobody called <see cref="StartAsync"/>.
    /// </para>
    /// </remarks>
    private void RequireStarted(string operation)
    {
        if (StartedUtc is null)
        {
            throw new InvalidOperationException(
                $"The broker has not started. Call StartAsync before {operation}: until the " +
                "attach sequence runs, this agent's debts and its liveness still describe " +
                "its previous session, and this operation would decide against evidence that " +
                "attaching exists to invalidate.");
        }
    }

    /// <summary>
    /// Opens an exchange and says the question in it, as one act.
    /// </summary>
    /// <param name="from">The agent asking.</param>
    /// <param name="to">The agent expected to answer.</param>
    /// <param name="responderModel">
    /// What produced this question. Required on every message, not only answers; see
    /// SPEC.md §11.3.
    /// </param>
    /// <param name="payloadJson">
    /// The question document. Rule 1 requires it to state what the asker will do with each
    /// possible answer, and rule 8.5 caps it at 2,000 characters; both are enforced in storage,
    /// so a payload breaking either arrives here as a <see cref="SqliteException"/> and nothing
    /// is written.
    /// </param>
    /// <param name="nowUtc">The instant of the ask.</param>
    /// <param name="conversationId">
    /// The discussion this question continues, or <see langword="null"/> if it stands alone.
    /// See SPEC.md §5.0.
    /// </param>
    /// <param name="cancellationToken">Cancels the ask.</param>
    /// <returns>
    /// The exchange and the question, or the refusal with whatever the asker can act on.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="from"/> and <paramref name="to"/> are the same agent;
    /// <paramref name="responderModel"/> or <paramref name="payloadJson"/> is empty; or
    /// <paramref name="responderModel"/> claims the <c>parley:</c> namespace reserved by
    /// SPEC.md §11.3.
    /// </exception>
    /// <exception cref="SqliteException">
    /// The question violates a storage constraint. Nothing is written, including the exchange.
    /// </exception>
    /// <remarks>
    /// <para>
    /// One transaction spanning both writes, and that is the whole reason this method exists
    /// rather than leaving callers to make the two calls themselves. SPEC.md §10.1 has
    /// <c>ask</c> as one operation; done as two commits, a question the schema refuses leaves
    /// an exchange open with nothing in it, holding a pair slot and an entry in the
    /// distinctness index. It waits out its delivery window and is finally reported to the
    /// asker as <c>ghosted</c> — nobody came — when nobody was ever asked. Five of them
    /// saturate the pair under §7.1 and lock it out.
    /// </para>
    /// <para>
    /// The policy comes from <see cref="BrokerOptions.ExchangePolicy"/> and there is
    /// deliberately no parameter for it. This is the agent-facing surface, and §7.1's caps are
    /// controls rather than tuning knobs: a control the controlled party selects is not a
    /// control. An operator configures the broker; an agent takes the terms it is given.
    /// </para>
    /// <para>
    /// Admission runs before the question is written, so a refused ask writes nothing at all —
    /// not even the question that was refused. That is deliberate, and it is the one place
    /// where the log is not a complete record of what agents tried to say. Storing refused
    /// questions would make the message table a place an agent can write to without an exchange
    /// to hold it, which §5 forbids by making <c>exchange_id</c> mandatory for every type but
    /// <c>notify</c>.
    /// </para>
    /// </remarks>
    public async Task<AskOutcome> AskAsync(
        AgentName from,
        AgentName to,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("asking");
        RequireAdapterResponder(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);
        await RequireRegisteredAsync(from, cancellationToken).ConfigureAwait(false);

        using var scope = WriteScope.Begin(_connection, ambient: null);

        var admission = await _exchanges.TryOpenAsync(
            from,
            to,
            nowUtc,
            MessageIdentity.ComputeQuestion(from, to, payloadJson),
            _options.ExchangePolicy,
            conversationId,
            scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        if (!admission.Succeeded)
        {
            return AskOutcome.Refused(admission);
        }

        var appended = await _messages.AppendAsync(
            admission.Exchange!.Id,
            MessageType.Question,
            from,
            to,
            responderModel,
            payloadJson,
            nowUtc,
            conversationId,
            scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        scope.Complete();

        // The append cannot be suppressed as a duplicate, which is why nothing branches on it:
        // a question's identity includes the exchange it belongs to, and this exchange was
        // created a few lines ago. A repeated question is caught a step earlier by the
        // distinctness index, which refuses the admission above. See AskOutcome for why no flag
        // is exposed for a value that could only ever read one way.
        return AskOutcome.Asked(admission, appended.Message);
    }

    /// <summary>
    /// Requests detail within an open exchange, or supplies detail that was requested.
    /// </summary>
    /// <param name="exchangeId">The exchange, as returned by <see cref="AskAsync"/>.</param>
    /// <param name="from">The agent speaking. Must hold the turn.</param>
    /// <param name="responderModel">What produced this message; see SPEC.md §11.3.</param>
    /// <param name="payloadJson">The clarification document.</param>
    /// <param name="nowUtc">The instant of the clarification.</param>
    /// <param name="cancellationToken">Cancels the clarification.</param>
    /// <returns>The message as stored, or why the exchange refused it.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="responderModel"/> or <paramref name="payloadJson"/> is empty, or
    /// <paramref name="responderModel"/> claims the <c>parley:</c> namespace reserved by
    /// SPEC.md §11.3.
    /// </exception>
    /// <exception cref="SqliteException">
    /// The message violates a storage constraint. Nothing is written, including the transition.
    /// </exception>
    /// <remarks>
    /// <para>
    /// One operation covering both directions, because SPEC.md §10.1 has one. Which direction
    /// this is depends only on who is speaking, and the exchange already knows: a request from
    /// the answerer increments the depth against §7's cap, while the asker supplying what was
    /// asked for does not, since that is not a new round.
    /// </para>
    /// <para>
    /// A breach of the clarify cap is refused here rather than closing the exchange. SPEC.md §7
    /// has the broker call <see cref="Exchange.ForceUnknown"/> at that point, which is a
    /// decision this method does not make on the agent's behalf — an agent that has exhausted
    /// its budget should learn that, not have the exchange closed underneath it by the same
    /// call it was refused by.
    /// </para>
    /// </remarks>
    public Task<SayOutcome> ClarifyAsync(
        long exchangeId,
        AgentName from,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        SayAsync(
            exchangeId,
            from,
            MessageType.Clarify,
            responderModel,
            payloadJson,
            nowUtc,
            (exchange, speaker) => exchange.Clarify(speaker, nowUtc),
            cancellationToken);

    /// <summary>
    /// Closes an open exchange with an answer, releasing its pair slot.
    /// </summary>
    /// <param name="exchangeId">The exchange, as returned by <see cref="AskAsync"/>.</param>
    /// <param name="from">
    /// The agent answering. Must be the exchange's answerer and must hold the turn.
    /// </param>
    /// <param name="responderModel">What produced this answer; see SPEC.md §11.3.</param>
    /// <param name="payloadJson">
    /// The answer document, carrying the per-sub-question resolutions.
    /// </param>
    /// <param name="nowUtc">The instant of the answer.</param>
    /// <param name="resolution">
    /// The rolled-up outcome, taking the least resolved of the per-sub-question resolutions.
    /// <see cref="Core.Resolution.Ghosted"/> is refused: only the broker records that.
    /// </param>
    /// <param name="isDecision">
    /// <see langword="true"/> if this answer settles something worth promoting to a durable
    /// decision record.
    /// </param>
    /// <param name="cancellationToken">Cancels the answer.</param>
    /// <returns>The answer as stored, or why the exchange refused it.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="responderModel"/> or <paramref name="payloadJson"/> is empty, or
    /// <paramref name="responderModel"/> claims the <c>parley:</c> namespace reserved by
    /// SPEC.md §11.3.
    /// </exception>
    /// <exception cref="SqliteException">
    /// The message violates a storage constraint. Nothing is written, including the closure.
    /// </exception>
    /// <remarks>
    /// The atomicity matters more here than anywhere else. Closing the exchange and writing the
    /// answer as two commits admits a failure in which the exchange is closed <c>answered</c>
    /// and the answer itself was never written — so the asker learns its question is settled
    /// and has no way to find out what the settlement was, with the exchange in a terminal
    /// state no replay reaches. SPEC.md §14's grace period cannot help: it disposes of
    /// exchanges nobody answered, and this one was answered.
    /// </remarks>
    public Task<SayOutcome> AnswerAsync(
        long exchangeId,
        AgentName from,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        Resolution resolution = Resolution.Answered,
        bool isDecision = false,
        CancellationToken cancellationToken = default) =>
        SayAsync(
            exchangeId,
            from,
            MessageType.Answer,
            responderModel,
            payloadJson,
            nowUtc,
            (exchange, speaker) => exchange.Answer(speaker, nowUtc, resolution, isDecision),
            cancellationToken);

    /// <summary>
    /// Applies a transition to an open exchange and writes the message announcing it, as one act.
    /// </summary>
    /// <param name="exchangeId">The exchange to move.</param>
    /// <param name="from">The agent speaking.</param>
    /// <param name="type">The message kind to write.</param>
    /// <param name="responderModel">What produced the message.</param>
    /// <param name="payloadJson">The message document.</param>
    /// <param name="nowUtc">The instant of the move.</param>
    /// <param name="transition">The domain transition to attempt.</param>
    /// <param name="cancellationToken">Cancels the move.</param>
    /// <returns>The message as stored, or why the move was refused.</returns>
    /// <remarks>
    /// <para>
    /// The read is inside the transaction, not before it. Deciding whether a move is legal
    /// against a row another writer can change in between is the mistake the version stamp
    /// exists to catch, and catching it there costs a wasted round trip the write lock avoids.
    /// </para>
    /// <para>
    /// The recipient is derived rather than passed. Both message types travel to whichever
    /// participant is not speaking, and a caller that could name the recipient could address a
    /// message on this exchange to somebody outside it — which the schema would accept, since
    /// its foreign keys check that both agents are registered, not that either is in this
    /// exchange.
    /// </para>
    /// <para>
    /// The conversation identifier is not passed either. An exchange already belongs to one, and
    /// SPEC.md §5.0 makes a clarification or answer inherit it rather than declare it; the
    /// composite foreign key on <c>messages</c> would refuse a different one, but the reason to
    /// leave it null here is that a participant does not get to decide which discussion the
    /// exchange it is answering belongs to.
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
        CancellationToken cancellationToken)
    {
        RequireStarted(type == MessageType.Answer ? "answering" : "clarifying");
        RequireAdapterResponder(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        using var scope = WriteScope.Begin(_connection, ambient: null);

        var exchange = await _exchanges.GetAsync(exchangeId, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        if (exchange is null)
        {
            // Reported as "not open" rather than "no such exchange", so an agent cannot use this
            // to find out which identifiers exist. See SayOutcome.
            return SayOutcome.Refused(TransitionError.ExchangeNotOpen);
        }

        // Decided before anything is written: the recipient below is derived from participants
        // this establishes are real, and an agent outside the exchange is refused here.
        var moved = transition(exchange, from);

        if (!moved.Succeeded)
        {
            return SayOutcome.Refused(moved.Error);
        }

        var to = from == exchange.Asker ? exchange.Answerer : exchange.Asker;

        var appended = await _messages.AppendAsync(
            exchangeId,
            type,
            from,
            to,
            responderModel,
            payloadJson,
            nowUtc,
            conversationId: null,
            scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        if (!appended.Stored)
        {
            // A transport retry: this exact message was already written inside the §13.1
            // window, which means the transition it announces was already applied. Applying it
            // again would advance the exchange on a message nobody received — the clarify depth
            // charged twice against §7's cap, and the turn handed to a peer whose inbox gained
            // nothing, so it holds an obligation it has no way to see.
            //
            // Returning without saving is what AppendOutcome already prescribes: carry on as
            // though the send succeeded, because it did, once. The transaction rolls back, so
            // the transition applied to the in-memory instance goes with it.
            return SayOutcome.Said(appended.Message, stored: false);
        }

        if (!await _exchanges.SaveAsync(
                exchange, decisionRef: null, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            // Unreachable, and thrown rather than reported. The version guard on SaveAsync can
            // only refuse a row that changed since it was read, and the read above happened
            // inside this transaction, which took the write lock before it ran — so a mismatch
            // here means the guarantee that makes every check in this method sound has failed,
            // which is a defect and not an outcome an agent can act on. The transaction rolls
            // back with the message in it.
            //
            // A race between two brokers does not arrive here: it resolves one step earlier,
            // when the loser reads what the winner committed and the transition is refused
            // against a closed exchange. That is covered by test, which is what keeps this from
            // being an assumption.
            throw new InvalidOperationException(
                $"Exchange {exchangeId} changed under a write lock held for this operation. " +
                "This indicates the connection is not honouring IMMEDIATE transactions.");
        }

        scope.Complete();
        return SayOutcome.Said(appended.Message, appended.Stored);
    }

    /// <summary>
    /// Announces an event to a peer. Opens no exchange and creates no debt.
    /// </summary>
    /// <param name="from">The agent announcing.</param>
    /// <param name="to">The agent being told.</param>
    /// <param name="responderModel">What produced this; see SPEC.md §11.3.</param>
    /// <param name="payloadJson">The event document.</param>
    /// <param name="nowUtc">The instant of the announcement.</param>
    /// <param name="conversationId">
    /// The discussion this belongs to, or <see langword="null"/> if it stands alone. A
    /// notification has no exchange, so unlike a clarification or an answer it declares its own.
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The event as stored, or the refusal.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="from"/> and <paramref name="to"/> are the same agent;
    /// <paramref name="responderModel"/> or <paramref name="payloadJson"/> is empty; or
    /// <paramref name="responderModel"/> claims the <c>parley:</c> namespace reserved by §11.3.
    /// </exception>
    /// <exception cref="SqliteException">
    /// The event violates a storage constraint — an over-length payload, or one that is not
    /// valid JSON.
    /// </exception>
    /// <remarks>
    /// <para>
    /// SPEC.md §5.4: no reply is expected. Every type is fire-and-forget in the sense that the
    /// sender does not wait; what distinguishes this one is that nothing is owed in return. It
    /// takes no pair slot, so §7.1's cap does not apply, and it cannot deadlock, so §7.2's cycle
    /// check has nothing to check — a notification adds no edge to the wait-for graph because
    /// nobody is waiting.
    /// </para>
    /// <para>
    /// One write, but still one transaction. There is no exchange to keep the message consistent
    /// with, so the span buys less here than it does on <see cref="AskAsync"/> — what it buys is
    /// that a refusal cannot mint the conversation it was refused from, and that the closed check
    /// and the append cannot straddle somebody else's conclusion. The window that second one
    /// closes is narrow and its outcome would have been benign, since §5.0 lets a message already
    /// written stay where it is; the reason to close it anyway is that an operation which checked
    /// in one transaction and wrote in another would be the odd one out, and odd ones get copied.
    /// </para>
    /// <para>
    /// Deduplication is the only guard it has, and §13.1 says so explicitly — a repeated question
    /// is refused by the pair slot, a repeated clarification by the turn check, a repeated answer
    /// by the exchange no longer being open, and <c>Notify</c> by nothing else at all. An
    /// at-least-once transport will retry, so a caller should read
    /// <see cref="NotifyOutcome.Stored"/> rather than assuming this wrote something.
    /// </para>
    /// <para>
    /// Routing is refused for the statuses §14 excludes, the same as a question. It matters less
    /// than it does there — nobody is left waiting on a debt — but a sender told plainly that a
    /// peer is gone can stop announcing to it, and one whose writes silently succeed cannot.
    /// </para>
    /// </remarks>
    public async Task<NotifyOutcome> NotifyAsync(
        AgentName from,
        AgentName to,
        string responderModel,
        string payloadJson,
        DateTimeOffset nowUtc,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("notifying");
        RequireAdapterResponder(responderModel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);
        await RequireRegisteredAsync(from, cancellationToken).ConfigureAwait(false);

        // One transaction across the checks and the write, for the reason WriteScope exists:
        // AskAsync spans its own, and an operation that checked in one transaction and wrote in
        // another would be the odd one out that later gets copied. The window here is narrow and
        // its outcome benign — an event admitted a moment before a conclusion is the same as one
        // admitted a moment after, and §5.0 lets a message already written stay where it is —
        // but "narrow and benign" is not a property to leave resting on nothing.
        using var scope = WriteScope.Begin(_connection, ambient: null);

        if (await _agents.IsUnroutableAsync(to, scope.Transaction, cancellationToken)
            .ConfigureAwait(false))
        {
            return NotifyOutcome.Refused(NotifyRejection.RecipientUnroutable);
        }

        if (await _agents.IsRetiredAsync(from, scope.Transaction, cancellationToken)
            .ConfigureAwait(false))
        {
            return NotifyOutcome.Refused(NotifyRejection.SenderRetired);
        }

        if (conversationId is not null)
        {
            // §5.0 words its refusal in terms of a Question, because that is what takes a slot
            // and grows membership. But the hazard it names is a message sitting "in a closed
            // discussion nobody may add to", and a notification does that just as well — a probe
            // found the question refused and the notification beside it admitted.
            var conversation = await _conversations.GetAsync(
                conversationId, scope.Transaction, cancellationToken).ConfigureAwait(false);

            if (conversation?.State == ConversationState.Closed)
            {
                return NotifyOutcome.Refused(NotifyRejection.ConversationClosed);
            }

            // Declared rather than joined. §5.0 has a Notify declare its own conversation, since
            // it has no exchange to inherit one from — but membership is derived from being party
            // to an exchange, and a notification is not one. So this mints the discussion without
            // making anybody a participant of it, which is what keeps a notification from
            // conferring a vote in a closure poll.
            await _conversations.EnsureOpenAsync(
                conversationId, nowUtc, scope.Transaction, cancellationToken).ConfigureAwait(false);
        }

        var appended = await _messages.AppendAsync(
            exchangeId: null,
            MessageType.Notify,
            from,
            to,
            responderModel,
            payloadJson,
            nowUtc,
            conversationId,
            scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        scope.Complete();
        return NotifyOutcome.Sent(appended.Message, appended.Stored);
    }

    /// <summary>
    /// Lists the agents this one may address.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every registered agent an operator has not retired.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <remarks>
    /// <para>
    /// SPEC.md §10.1's <c>agents()</c>, and the only operation that tells an agent anything about
    /// another. It is what makes referral work without leaking: rule 9 has an agent that does not
    /// own part of an answer name whoever does, and §11.3 notes that naming a peer discloses
    /// nothing this already lists. An agent that could not enumerate its peers would have to be
    /// told them in its prompt, which goes stale the moment the roster changes.
    /// </para>
    /// <para>
    /// Returns a projection, not the stored record. The registry also holds a workspace path, a
    /// session handle and the adapter's own configuration, and none of those may cross to a
    /// peer — see <see cref="AgentSummary"/>, which exists so that the omission is a type rather
    /// than a habit.
    /// </para>
    /// <para>
    /// Guarded on start like everything else here, because the liveness it reports is exactly
    /// what §14's attach sequence invalidates for the attaching agent: before it runs, this
    /// agent still carries whatever its previous session left, and an asker reading its own
    /// stale mark would route on evidence about a process that is gone.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        CancellationToken cancellationToken = default)
    {
        RequireStarted("listing agents");

        return await _agents.ListRoutableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Says whether the caller is finished with a discussion, opening a closure poll if nobody
    /// had, or answering one if somebody had.
    /// </summary>
    /// <param name="conversationId">The discussion.</param>
    /// <param name="agent">The participant speaking.</param>
    /// <param name="finished">
    /// <see langword="true"/> to agree the discussion is over; <see langword="false"/> to
    /// decline, which blocks closure.
    /// </param>
    /// <param name="nowUtc">The instant of the call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the discussion is now closed, or a refusal.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <remarks>
    /// <para>
    /// One operation rather than two, because proposing and answering are the same statement.
    /// An agent that says it is finished has proposed closure if nobody had yet, and has
    /// answered the poll if somebody had; nothing distinguishes those from the agent's side,
    /// and splitting them would ask it to know which situation it was in — which it cannot,
    /// since SPEC.md §5.0 tells it nothing about the poll it is not part of.
    /// </para>
    /// <para>
    /// Nobody moderates. The broker runs the poll, no agent runs it, and no agent's proposal
    /// carries more weight than another's — an agent that could conclude a discussion on
    /// everyone's behalf would be the lead principle 2.6 forbids. A decline therefore blocks
    /// outright rather than being outvoted, because a majority that can conclude over an
    /// objection is authority over another agent by a different name.
    /// </para>
    /// <para>
    /// Declining is cheap to reverse and the agent need not remember it. The vote is durable,
    /// but §5.0 has the broker re-deliver the closure question when the conversation next falls
    /// idle, and calling again with <paramref name="finished"/> set overwrites the earlier
    /// answer. An agent that declined and has since been satisfied simply says so.
    /// </para>
    /// <para>
    /// A closed conversation returns <see langword="true"/> without recording anything, so
    /// calling twice is safe and says the same thing both times. That is the whole of what a
    /// participant may learn: §5.0 shares outcomes and withholds tallies, and this reports one
    /// boolean about a discussion the caller is already in.
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

        // Membership is checked before anything else is read, and its refusal is the only one
        // this operation has. An agent that is not a participant learns nothing at all — not
        // whether the conversation exists, not who is in it, not whether a poll is running.
        if (!await _conversations.IsParticipantAsync(
                conversationId, agent, scope.Transaction, cancellationToken).ConfigureAwait(false))
        {
            return ClosureOutcome.NotAParticipant();
        }

        var conversation = await _conversations.GetAsync(
            conversationId, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (conversation is null)
        {
            // Unreachable: a participant row carries a foreign key into this table, so a
            // participant implies a conversation. Kept because the alternative to a refusal
            // here is a null dereference below, and treating it as "not a participant" keeps
            // the disclosure story intact if that ever changes.
            return ClosureOutcome.NotAParticipant();
        }

        if (conversation.State == ConversationState.Closed)
        {
            return ClosureOutcome.Recorded(concluded: true);
        }

        var silence = _options.ExchangePolicy.GracePeriod;

        // Declining when nothing is being decided says only that the caller is not finished,
        // which is what an open conversation already means. Opening a poll on it would be
        // worse than pointless: every other participant would be asked a closure question
        // nobody proposed, and under SPEC.md §5.0 their silence would then be recorded as
        // assent to it. §5.0 ties proposing to an agent that "says it is finished", and this
        // is the other half of that sentence.
        if (conversation.State == ConversationState.Open && !finished)
        {
            return ClosureOutcome.Recorded(concluded: false);
        }

        // Idempotent on an already-polling conversation: it stamps only participants not yet
        // asked. A proposer holding no open exchange is not among them — see
        // ConversationRepository.BeginPollAsync for why that exemption cannot exist.
        await _conversations.BeginPollAsync(
            conversationId, nowUtc, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        await _conversations.RecordVoteAsync(
            conversationId, agent, finished ? Vote.Done : Vote.NotDone, nowUtc,
            scope.Transaction, cancellationToken).ConfigureAwait(false);

        var concluded = await _conversations.TryConcludeAsync(
            conversationId, nowUtc, silence, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        scope.Complete();
        return ClosureOutcome.Recorded(concluded);
    }

    /// <summary>
    /// Concludes every conversation whose poll has been satisfied.
    /// </summary>
    /// <param name="nowUtc">The instant to judge silence against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many conversations closed.</returns>
    /// <remarks>
    /// <para>
    /// This is what makes silence assent. A poll that everybody answered concludes on the last
    /// vote without waiting for a tick; a poll one participant ignored concludes here, once its
    /// window has elapsed. Without the second, one unreachable agent could hold a discussion
    /// open indefinitely — which SPEC.md §5.0 refuses precisely because the alternative gives an
    /// absent agent more power than a present one.
    /// </para>
    /// <para>
    /// A conversation whose last live blocker was retired or marked unreachable also concludes
    /// here, with no vote changing. §5.0 lapses a decline with its holder, and nothing else
    /// would notice that the registry moved.
    /// </para>
    /// </remarks>
    private async Task<int> ConcludeSatisfiedPollsAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var polling = await _conversations.GetPollingAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var closed = 0;

        foreach (var conversationId in polling)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _conversations.TryConcludeAsync(
                    conversationId, nowUtc, _options.ExchangePolicy.GracePeriod,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                closed++;
            }
        }

        return closed;
    }

    /// <summary>
    /// Attaches an agent to the broker, running the sequence of SPEC.md §14.
    /// </summary>
    /// <param name="agent">The agent this broker speaks for.</param>
    /// <param name="nowUtc">The instant of the attach.</param>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>What the sequence did.</returns>
    /// <exception cref="InvalidOperationException">
    /// No agent by that name is registered.
    /// </exception>
    /// <remarks>
    /// Call once per process lifetime. A second call is not refused, because refusing one would
    /// break the case this exists for, but it moves <see cref="StartedUtc"/> forward and with it
    /// the ghosting clock — so a host that calls it twice by accident silently delays ghosting
    /// for every orphan, with nothing to notice.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>Scoped to the attaching agent.</b> It is marked <c>live</c>, and the exchanges it owes
    /// are orphaned. Every other agent and every other exchange is left exactly as it was.
    /// </para>
    /// <para>
    /// This replaced a broker-wide sequence that marked all agents offline and orphaned every
    /// open exchange, and the reason is worth keeping. That was right while a broker was one
    /// long-running service hosting all of its agents: they shared a fate, so a restart
    /// invalidated everything at once. An agent is now a coding session, started and stopped by
    /// whoever is working, and §11.1's adapters attach one agent each — so under the old rule
    /// the second agent to open a session orphaned the first's live work, the third orphaned
    /// both, and none of them could tell. A probe with two agents showed it in three calls;
    /// twenty-two repositories with an agent apiece is the deployment §1 describes.
    /// </para>
    /// <para>
    /// <b>What it owes, not what it is party to.</b> An exchange this agent is <em>waiting on</em>
    /// has somebody else working towards its deadline, and orphaning it would discard a peer's
    /// work in progress because a process on the other side of the conversation restarted. What
    /// has no worker is the debt the vanished agent was carrying, which is what <c>owed_by</c>
    /// names and what orphaning is for.
    /// </para>
    /// <para>
    /// Marking <c>live</c> does not disturb a retirement: <see cref="AgentRepository.MarkLiveAsync"/>
    /// refuses a retired agent, because retirement is a person's judgement and attaching is not
    /// evidence against it. An agent whose registration was withdrawn can start its server all
    /// it likes and will still be refused every operation.
    /// </para>
    /// <para>
    /// Orphaning goes through the domain transition one exchange at a time rather than a bulk
    /// update, which is slower and correct for two reasons. The transition is where the rule
    /// lives, and it stamps the instant of orphaning onto <c>expires_utc</c>, which is what the
    /// grace period is later measured from. A bulk <c>UPDATE</c> would have to reproduce both,
    /// and reproducing a rule is how the two copies come to disagree.
    /// </para>
    /// </remarks>
    public async Task<StartResult> StartAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        // Refused before anything is written. An adapter configured with a name the registry
        // does not hold would otherwise attach cleanly, orphan nothing, mark nothing live, and
        // then throw on every single operation — reporting a deployment fault to the model
        // instead of to the person who wrote the launch configuration. It also keeps
        // StartResult.AgentMarkedLive unambiguous: a zero now means retired, and only retired.
        await RequireRegisteredAsync(agent, cancellationToken).ConfigureAwait(false);

        // No session reference: SPEC.md §12 has that carry whatever an adapter needs to reach
        // an agent again, and an adapter the agent launched itself has nothing to hand back —
        // it is reached by being running, not by being called.
        var live = await _agents.MarkLiveAsync(agent, sessionRef: null, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        var owed = await _exchanges.GetOpenOwedByAsync(agent, cancellationToken)
            .ConfigureAwait(false);

        var orphaned = 0;

        foreach (var exchange in owed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!exchange.Orphan(nowUtc).Succeeded)
            {
                continue;
            }

            if (await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                orphaned++;
            }
        }

        Attached = agent;

        // Recorded last, so a sequence that throws part-way leaves a broker that refuses to
        // tick rather than one that ticks having half-orphaned its exchanges. TickAsync reads
        // this to time ghosting, and a start that did not finish is not a start.
        StartedUtc = nowUtc;

        return new StartResult(live, orphaned);
    }

    /// <summary>
    /// Records that this broker's agent is stopping.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if the agent was marked offline.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The counterpart to attaching, and deliberately the only thing this does. It does not
    /// orphan the agent's debts: those belong to work that may well be finished by whoever picks
    /// the session up next, and the attach sequence releases them on the way back in — which is
    /// the moment it is actually known that nobody carried on.
    /// </para>
    /// <para>
    /// Best-effort, because a crashed process calls nothing. That is affordable: the mark is
    /// cosmetic, since <c>offline</c> does not gate routing. What is <em>not</em> left to a
    /// clean shutdown is anything a peer depends on — deadlines are enforced by the sweep, and
    /// debts are released on attach.
    /// </para>
    /// </remarks>
    public async Task<bool> DetachAsync(CancellationToken cancellationToken = default)
    {
        RequireStarted("detaching");

        return await _agents.MarkOfflineAsync(Attached!.Value, cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Runs one maintenance pass.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate deadlines against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass did to exchanges and to conversations.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <remarks>
    /// Refuses to run before the attach sequence, because the ghosting clock is measured from
    /// broker start and a sweep without one would fall back to wall-clock timing — ghosting
    /// every orphan that outlived an outage the instant the process came back, which is the
    /// exact failure the clock rule exists to prevent. Failing loudly beats a sweep that looks
    /// like it worked.
    /// </remarks>
    public async Task<TickResult> TickAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (StartedUtc is not { } startedUtc)
        {
            throw new InvalidOperationException(
                "The broker has not started. Call StartAsync first: ghosting is measured from " +
                "broker start, and a sweep without one would ghost every orphan that outlived " +
                "an outage as soon as the process returned.");
        }

        var swept = await _reaper.SweepAsync(nowUtc, startedUtc, cancellationToken)
            .ConfigureAwait(false);

        // After the exchange sweep, not before. Closure is decided partly from which
        // participants still hold open exchanges, so running it first would judge a poll
        // against exchanges this very tick was about to orphan — concluding a discussion on
        // the strength of a deadline that had not yet been applied.
        var concluded = await ConcludeSatisfiedPollsAsync(nowUtc, cancellationToken)
            .ConfigureAwait(false);

        return new TickResult(swept, concluded);
    }

    /// <summary>
    /// Drains an agent's inbox, delivering what is waiting for it.
    /// </summary>
    /// <param name="agent">The agent collecting its messages.</param>
    /// <param name="nowUtc">The instant of the drain.</param>
    /// <param name="limit">The most messages to return, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>
    /// The messages delivered, debts first, together with any closure poll outstanding against
    /// this agent.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the inbound traffic SPEC.md §14 describes, so it does the replay before the
    /// read. Nothing is replayed at attach: the sequence orphans what this agent owes, and the
    /// agent collecting its messages is what brings those back, so a deployment does not pay to
    /// instantiate agents that would have stayed idle. Under a resume policy of
    /// <see cref="Core.ResumePolicy.Never"/> the replay finds nothing and the drain is simply a
    /// read.
    /// </para>
    /// <para>
    /// Delivery is recorded on two rows and both matter. The message is marked delivered, which
    /// is what stops it being handed out again. The exchange is told, which starts its working
    /// window — and that is the whole reason §6 could split one deadline into two without
    /// inventing an acknowledgement message. Until this runs, silence means nobody collected the
    /// question; afterwards it means somebody has it and is working.
    /// </para>
    /// <para>
    /// A Notify has no exchange and only the message is marked. It is the one type where
    /// delivery says nothing about anybody's deadline, because nothing is owed in return.
    /// </para>
    /// <para>
    /// Delivery is recorded per message rather than in one write at the end. A drain
    /// interrupted half way has delivered what it returned and nothing more, which is the
    /// property a caller needs: it may act on what it received without knowing whether the rest
    /// arrived.
    /// </para>
    /// </remarks>
    public async Task<Inbox> DrainInboxAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("draining an inbox");

        // Traffic for this agent is what triggers the replay, so it happens before the read and
        // anything it brings back is in the drain that asked for it rather than the next one.
        await ResumeOwedByAsync(agent, nowUtc, cancellationToken).ConfigureAwait(false);

        var pending = await _messages.GetPendingAsync(agent, limit, cancellationToken)
            .ConfigureAwait(false);

        var delivered = new List<DeliveredMessage>(pending.Count);

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The window is started before the message is stamped, and the order is the whole
            // point rather than an accident. These are two writes and nothing makes them atomic,
            // so one of them can be the last thing that happens, and the two failures are not
            // equally bad.
            //
            // This way round, a crash in between leaves an exchange with its long window and a
            // message still pending: the next drain hands it over again, which costs the agent a
            // repeated question. The other way round leaves a message marked delivered against
            // an exchange still on its short window, so it orphans within minutes while the
            // agent is working on it, and the answer that eventually arrives is refused because
            // the exchange is no longer open. A repeated question is recoverable; a discarded
            // answer is not.
            if (message.ExchangeId is { } exchangeId)
            {
                await StartWorkingWindow(exchangeId, nowUtc, cancellationToken).ConfigureAwait(false);
            }

            if (!await _messages.MarkDeliveredAsync(message.Id, nowUtc, cancellationToken)
                .ConfigureAwait(false))
            {
                // Already delivered by a drain that raced this one. Skipped rather than returned
                // twice, because a caller acting on the same question twice is the failure the
                // delivered stamp exists to prevent.
                //
                // Unreachable as things stand, and kept deliberately. Getting here needs a
                // second writer to stamp the row between the pending read and this write, which
                // needs two connections racing — something this type's threading contract
                // forbids. It is defence against a deployment that later relaxes that contract,
                // in the same spirit as the contention counter in Reaper.SweepAsync, and its
                // absence would turn that relaxation into duplicate questions rather than an
                // error.
                continue;
            }

            // Wrapped here and nowhere else. SPEC.md §13 makes this the broker's job precisely
            // because an agent permitted to label its own message would label it trusted, and an
            // adapter left to render the payload itself is one forgetful line away from the same
            // outcome. Handing back the envelope rather than the raw payload is what makes that
            // impossible rather than discouraged.
            //
            // A fresh token per message, not per drain: two messages sharing one would let a
            // sender that saw the first close the second's payload block.
            delivered.Add(new DeliveredMessage(
                message,
                TrustEnvelope.Wrap(
                    message.From,
                    message.ResponderModel,
                    message.PayloadJson,
                    TrustEnvelope.NewToken())));
        }

        // Read after the messages rather than before, because the replay above can join this
        // agent to a conversation it had fallen out of — and a poll it is newly entitled to see
        // should arrive in the drain that revealed it, not the next one.
        var polls = await _conversations.GetOutstandingPollsAsync(
            agent, _options.ExchangePolicy.GracePeriod, cancellationToken).ConfigureAwait(false);

        return new Inbox(delivered, polls);
    }

    /// <summary>
    /// Tells an exchange its question has been collected.
    /// </summary>
    /// <remarks>
    /// Silent when the exchange is no longer open. Between the pending read and here it may have
    /// been orphaned by a sweep or closed by its answerer, and neither is a reason to fail the
    /// drain — the message was genuinely delivered, and an exchange that is not open has no
    /// working window to start.
    /// <para>
    /// The state half of that guard is defensive rather than load-bearing:
    /// <see cref="Exchange.Deliver"/> refuses a closed exchange itself and leaves it unmutated,
    /// so removing the check changes only that a save would be issued for a row nothing
    /// changed. Worth keeping anyway, because that save still bumps the version stamp and would
    /// make a concurrent write fail for no reason. The null half is load-bearing and is not
    /// covered by a test, because a message whose exchange has vanished is something the foreign
    /// key makes impossible to construct.
    /// </para>
    /// </remarks>
    private async Task StartWorkingWindow(
        long exchangeId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var exchange = await _exchanges.GetAsync(
            exchangeId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (exchange is null || !exchange.Deliver(nowUtc).Succeeded)
        {
            return;
        }

        await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replays orphaned exchanges the owing agent has yet to answer.
    /// </summary>
    /// <param name="agent">The agent that owes the answers.</param>
    /// <param name="nowUtc">The instant of the replay.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>The exchanges returned to the open state.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called. Replay is defined against orphans and the
    /// attach sequence is what creates them, so before it runs this would report nothing to
    /// resume for an agent that owes answers — a wrong answer indistinguishable from a right one.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Lazy, per SPEC.md §14: nothing is replayed at attach. The sequence orphans what the
    /// attaching agent owes, and the first inbound traffic for it brings those back, so a
    /// deployment does not pay to instantiate agents that would have stayed idle.
    /// </para>
    /// <para>
    /// Returns an empty list under <see cref="Core.ResumePolicy.Never"/> and
    /// <see cref="Core.ResumePolicy.Ask"/> without consulting the gate. Never means the operator has
    /// said no already; ask means somebody must be asked, and asking is an operator surface this
    /// type does not have — so it declines rather than inventing consent. Both leave the orphans
    /// in place, and the grace period bounds how long that lasts.
    /// </para>
    /// <para>
    /// A replay whose question is already open again is skipped rather than replayed, and this
    /// is the one place where a refusal arrives as an exception rather than a false return.
    /// <see cref="ExchangeRepository.SaveAsync"/> reports a stale write by returning false, but
    /// here the write is not stale: a <em>different</em> row holds the same question, so the
    /// distinctness index raises a constraint violation. Catching it keeps one collision from
    /// abandoning the replay of everything else the agent owes.
    /// </para>
    /// <para>
    /// The re-ask therefore wins, which is what SPEC.md §17 open question 9 settles. The orphan
    /// stays orphaned and the grace period disposes of it. What that question leaves open is the
    /// incentive: the surviving re-ask starts at clarify depth zero while the orphan held the
    /// accumulated depth.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> ResumeOwedByAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        RequireStarted("resuming");

        if (_options.ResumePolicy != ResumePolicy.Always)
        {
            return [];
        }

        var orphans = await _exchanges.GetOrphanedOwedByAsync(agent, cancellationToken)
            .ConfigureAwait(false);

        var resumed = new List<Exchange>();

        foreach (var exchange in orphans)
        {
            // Defensive rather than load-bearing: every awaited call below already honours the
            // token, so removing this changes no observable behaviour today. It keeps the loop
            // cancellable independently of what the body happens to do, which a body that gained
            // a synchronous step would otherwise lose.
            cancellationToken.ThrowIfCancellationRequested();

            // A gate that throws is not caught, and the asymmetry with the constraint violation
            // below is deliberate. That violation is an expected outcome of ordinary operation —
            // an agent re-asked — so it is handled and the pass continues. A gate is the host's
            // own code, and an exception from it is a defect the host needs to see rather than
            // one the broker should absorb. Expected outcomes get handled; defects get surfaced.
            if (!await _options.ResumeGate.MayResumeAsync(exchange, cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            if (!exchange.Resume(nowUtc).Succeeded)
            {
                continue;
            }

            try
            {
                if (!await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }
            }
            catch (SqliteException ex)
                when (ex.SqliteExtendedErrorCode == UniqueConstraintViolation
                      && ex.Message.Contains("question_hash", StringComparison.Ordinal))
            {
                // The asker re-asked while this was orphaned, so an identical question is open
                // again and the distinctness index refuses the replay. That is the outcome
                // SPEC.md §17 open question 9 settles — the re-ask reflects current intent and
                // wins — but it arrives as an exception rather than a refused write, because it
                // is a different row that conflicts rather than this one being stale.
                //
                // Skipped rather than propagated: one collision must not abandon the replay of
                // every other exchange the agent owes. The orphan stays orphaned and the grace
                // period disposes of it.
                //
                // Narrowed to the distinctness index rather than any unique violation, because
                // this table carries another one and may gain more. Swallowing an unexpected
                // constraint failure would turn a schema defect into a replay that quietly did
                // nothing, which is the worst of both.
                continue;
            }

            // Reopening the exchange is only half a replay. SPEC.md §14 is explicit that a
            // replay is re-delivery rather than re-sending, so the message the agent owes an
            // answer to goes back into its inbox — the existing row, marked undelivered, not a
            // fresh one that would make the log a record of delivery attempts.
            //
            // Without this the exchange reopens and the agent has an obligation it cannot see:
            // its inbox is empty because the question is still stamped delivered from before the
            // interruption, while the broker waits for an answer nobody was asked for, until the
            // grace period ghosts it.
            var owed = await _messages
                .GetLatestOwedAsync(exchange.Id, agent, cancellationToken)
                .ConfigureAwait(false);

            if (owed is not null)
            {
                await _messages.RedeliverAsync(owed.Id, cancellationToken).ConfigureAwait(false);
            }

            resumed.Add(exchange);
        }

        return resumed;
    }
}
