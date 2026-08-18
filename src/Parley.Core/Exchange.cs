namespace Parley.Core;

/// <summary>
/// A single question between two agents and its resolution, including any clarification
/// rounds.
/// </summary>
/// <remarks>
/// <para>
/// This aggregate owns only what is true of one exchange in isolation: whose turn it is,
/// how many clarification rounds have been spent, when it expires, and whether it is still
/// live. Pair mutual exclusion and cycle rejection are broker-level concerns and live
/// elsewhere — see <see cref="WaitForGraph"/> and the partial unique index
/// <c>ux_open_pair</c> in the schema.
/// </para>
/// <para>
/// The asker and answerer roles are fixed at creation and never swap. The turn, tracked by
/// <see cref="OwedBy"/>, does swap: a clarification hands the turn back to the asker, and
/// the asker's reply returns it. Only the answerer may ever close the exchange.
/// </para>
/// <para>
/// Transitions report refusal through <see cref="TransitionResult"/> rather than throwing.
/// An agent speaking out of turn is a routine event in a concurrent system, not an
/// exceptional one. Exceptions are reserved for arguments that could never be valid, such as
/// an agent addressing itself.
/// </para>
/// </remarks>
public sealed class Exchange
{
    /// <summary>
    /// The default number of clarification requests permitted before the broker forces an
    /// answer of <see cref="Resolution.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// This value is a starting estimate and is expected to be tuned against real clarify
    /// depth telemetry rather than argued about in advance.
    /// </remarks>
    public const int DefaultClarifyDepthCap = 3;

    private Exchange(
        long id,
        AgentName asker,
        AgentName answerer,
        TimeSpan ttl,
        int clarifyDepthCap,
        DateTimeOffset openedUtc)
    {
        Id = id;
        Asker = asker;
        Answerer = answerer;
        PairKey = PairKey.For(asker, answerer);
        OwedBy = answerer;
        State = ExchangeState.Open;
        Ttl = ttl;
        ClarifyDepthCap = clarifyDepthCap;
        OpenedUtc = openedUtc;
        ExpiresUtc = openedUtc + ttl;
    }

    /// <summary>
    /// Gets the storage identity of this exchange.
    /// </summary>
    /// <value>The primary key assigned when the exchange was inserted.</value>
    public long Id { get; }

    /// <summary>
    /// Gets the unordered pair key for the two participants.
    /// </summary>
    /// <value>The key against which this exchange holds the pair slot while open.</value>
    public PairKey PairKey { get; }

    /// <summary>
    /// Gets the agent that opened the exchange and awaits an answer.
    /// </summary>
    /// <value>The asking agent. Fixed for the life of the exchange.</value>
    public AgentName Asker { get; }

    /// <summary>
    /// Gets the agent that owes the closing answer.
    /// </summary>
    /// <value>
    /// The answering agent. Fixed for the life of the exchange, and the only agent permitted
    /// to call <see cref="Answer"/> — even during a clarification round when the turn sits
    /// with the asker.
    /// </value>
    public AgentName Answerer { get; }

    /// <summary>
    /// Gets the participant who must respond next.
    /// </summary>
    /// <value>
    /// Initially the <see cref="Answerer"/>. Flips on each <see cref="Clarify"/> call and is
    /// restored to the answerer when the exchange closes.
    /// </value>
    public AgentName OwedBy { get; private set; }

    /// <summary>
    /// Gets the current lifecycle state.
    /// </summary>
    /// <value>Open, closed, or orphaned.</value>
    public ExchangeState State { get; private set; }

    /// <summary>
    /// Gets the number of clarification requests spent so far.
    /// </summary>
    /// <value>
    /// A count of requests made by the <see cref="Answerer"/>. The asker's replies are not
    /// counted; see <see cref="Clarify"/> for why.
    /// </value>
    public int ClarifyDepth { get; private set; }

    /// <summary>
    /// Gets the clarification request limit for this exchange.
    /// </summary>
    /// <value>
    /// The number of requests permitted before <see cref="Clarify"/> refuses and the broker
    /// must force a resolution.
    /// </value>
    public int ClarifyDepthCap { get; }

    /// <summary>
    /// Gets the inactivity window granted after each message.
    /// </summary>
    /// <value>
    /// The duration added to the current time whenever the exchange is opened, clarified, or
    /// resumed, producing a new <see cref="ExpiresUtc"/>.
    /// </value>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// Gets the instant the exchange was opened.
    /// </summary>
    /// <value>The creation timestamp, in UTC. Never changes.</value>
    public DateTimeOffset OpenedUtc { get; }

    /// <summary>
    /// Gets the instant after which the exchange is eligible for orphaning.
    /// </summary>
    /// <value>
    /// A sliding deadline, extended by each clarification. An agent that disappears
    /// mid-question must not hold the pair slot indefinitely.
    /// </value>
    public DateTimeOffset ExpiresUtc { get; private set; }

    /// <summary>
    /// Gets the instant the exchange was closed.
    /// </summary>
    /// <value>
    /// The closing timestamp, or <see langword="null"/> if the exchange is not closed.
    /// </value>
    public DateTimeOffset? ClosedUtc { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the answer was flagged for promotion to a durable
    /// decision record.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the closing answer settled something worth writing down.
    /// </value>
    /// <remarks>
    /// Promotion is what stops the same boundary question being re-derived weeks later at
    /// full token cost, which is the failure the protocol exists to prevent.
    /// </remarks>
    public bool IsDecision { get; private set; }

    /// <summary>
    /// Gets the optimistic concurrency stamp last read from or written to storage.
    /// </summary>
    /// <value>
    /// Zero for an exchange that has never been saved; otherwise the value held by the
    /// stored row at the time this instance was loaded.
    /// </value>
    /// <remarks>
    /// This is frankly a persistence concern rather than a domain one, and it lives on the
    /// aggregate only because it has to round-trip with it. It exists because two paths that
    /// both load, transition, and save would otherwise silently lose one transition, and the
    /// transition most likely to be lost is a <see cref="ClarifyDepth"/> increment — which
    /// would quietly turn <see cref="ClarifyDepthCap"/> into a suggestion. Never interpret
    /// it as domain state.
    /// </remarks>
    public long Version { get; private set; }

    /// <summary>
    /// Records that this instance has been persisted, adopting the stamp the store assigned.
    /// </summary>
    /// <param name="version">The concurrency stamp now held by the stored row.</param>
    /// <remarks>
    /// Called by the persistence layer immediately after a successful save, so that a second
    /// save of the same instance carries the current stamp rather than a stale one. Adopting
    /// a stamp anywhere else silently defeats the optimistic concurrency check, which is why
    /// this is internal and reachable only through the <c>InternalsVisibleTo</c> grant to
    /// Parley.Store rather than left public with a warning attached to it.
    /// </remarks>
    internal void MarkSaved(long version) => Version = version;

    /// <summary>
    /// Determines whether an agent is a participant in this exchange.
    /// </summary>
    /// <param name="agent">The agent to test.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="agent"/> is the asker or the answerer;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool Involves(AgentName agent) => agent == Asker || agent == Answerer;

    /// <summary>
    /// Determines whether the exchange is overdue at a given instant.
    /// </summary>
    /// <param name="now">The instant to evaluate against, in UTC.</param>
    /// <returns>
    /// <see langword="true"/> if the exchange is still open and has passed
    /// <see cref="ExpiresUtc"/>; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Closed and orphaned exchanges never report as expired: they hold no pair slot, so
    /// there is nothing left for the reaper to release.
    /// </remarks>
    public bool IsExpiredAt(DateTimeOffset now) => State == ExchangeState.Open && now >= ExpiresUtc;

    /// <summary>
    /// Opens a new exchange, with the answerer owing the first response.
    /// </summary>
    /// <param name="id">The storage identity assigned to this exchange.</param>
    /// <param name="asker">The agent posing the question.</param>
    /// <param name="answerer">The agent expected to answer it.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="ttl">The inactivity window before the exchange may be orphaned.</param>
    /// <param name="clarifyDepthCap">
    /// The clarification request limit. Defaults to <see cref="DefaultClarifyDepthCap"/>.
    /// </param>
    /// <returns>The newly opened exchange.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="asker"/> and <paramref name="answerer"/> are the same agent.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ttl"/> is not positive, or <paramref name="clarifyDepthCap"/> is
    /// negative.
    /// </exception>
    /// <remarks>
    /// This constructs the aggregate only. Admission control — checking the pair slot is
    /// free and that the question would not close a cycle — belongs to the broker and must
    /// happen before this is called.
    /// </remarks>
    public static Exchange Open(
        long id,
        AgentName asker,
        AgentName answerer,
        DateTimeOffset nowUtc,
        TimeSpan ttl,
        int clarifyDepthCap = DefaultClarifyDepthCap)
    {
        if (asker == answerer)
        {
            throw new ArgumentException($"An agent cannot question itself ('{asker}').", nameof(answerer));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(clarifyDepthCap);

        return new Exchange(id, asker, answerer, ttl, clarifyDepthCap, nowUtc);
    }

    /// <summary>
    /// Sends a clarification, handing the turn to the other participant and extending the
    /// deadline.
    /// </summary>
    /// <param name="from">The agent sending the clarification. Must hold the turn.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <returns>
    /// <see cref="TransitionResult.Ok"/> on success, or a failure carrying
    /// <see cref="TransitionError.ExchangeNotOpen"/>,
    /// <see cref="TransitionError.NotAParticipant"/>,
    /// <see cref="TransitionError.NotYourTurn"/>, or
    /// <see cref="TransitionError.ClarifyDepthExceeded"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Clarification claims no pair slot. Allocating one would make the answerer unable to
    /// request detail, since the pair's only slot is already held by the question it is
    /// trying to answer.
    /// </para>
    /// <para>
    /// Depth increments only when the <see cref="Answerer"/> sends. The asker's reply is
    /// supplying detail that was asked for, not opening a new round, and charging both
    /// directions would halve the effective cap without saying so.
    /// </para>
    /// <para>
    /// When the cap is reached this refuses, and the broker is expected to call
    /// <see cref="ForceUnknown"/> to close the exchange rather than leaving it open.
    /// </para>
    /// </remarks>
    public TransitionResult Clarify(AgentName from, DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        if (!Involves(from))
        {
            return TransitionResult.Fail(TransitionError.NotAParticipant);
        }

        if (from != OwedBy)
        {
            return TransitionResult.Fail(TransitionError.NotYourTurn);
        }

        var isRequest = from == Answerer;
        if (isRequest && ClarifyDepth >= ClarifyDepthCap)
        {
            return TransitionResult.Fail(TransitionError.ClarifyDepthExceeded);
        }

        if (isRequest)
        {
            ClarifyDepth++;
        }

        OwedBy = from == Asker ? Answerer : Asker;
        ExpiresUtc = nowUtc + Ttl;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Closes the exchange with an answer, releasing the pair slot.
    /// </summary>
    /// <param name="from">
    /// The agent answering. Must be the <see cref="Answerer"/> and must hold the turn.
    /// </param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="isDecision">
    /// <see langword="true"/> if the answer settles something that should be promoted to a
    /// durable decision record.
    /// </param>
    /// <returns>
    /// <see cref="TransitionResult.Ok"/> on success, or a failure carrying
    /// <see cref="TransitionError.ExchangeNotOpen"/>,
    /// <see cref="TransitionError.NotAParticipant"/>,
    /// <see cref="TransitionError.OnlyAnswererMayAnswer"/>, or
    /// <see cref="TransitionError.NotYourTurn"/>.
    /// </returns>
    /// <remarks>
    /// The per-sub-question <see cref="Resolution"/> values travel on the message payload,
    /// not on the aggregate. The exchange tracks whether it closed, not what it concluded.
    /// </remarks>
    public TransitionResult Answer(AgentName from, DateTimeOffset nowUtc, bool isDecision = false)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        if (!Involves(from))
        {
            return TransitionResult.Fail(TransitionError.NotAParticipant);
        }

        if (from != Answerer)
        {
            return TransitionResult.Fail(TransitionError.OnlyAnswererMayAnswer);
        }

        if (from != OwedBy)
        {
            return TransitionResult.Fail(TransitionError.NotYourTurn);
        }

        State = ExchangeState.Closed;
        ClosedUtc = nowUtc;
        IsDecision = isDecision;
        OwedBy = Answerer;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Closes the exchange unresolved after the clarification cap has been breached.
    /// </summary>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <returns>
    /// <see cref="TransitionResult.Ok"/> on success, or a failure carrying
    /// <see cref="TransitionError.ExchangeNotOpen"/>.
    /// </returns>
    /// <remarks>
    /// This deliberately bypasses the turn check that <see cref="Answer"/> applies. At the
    /// moment the cap is breached the answerer is mid-clarification and the turn sits with
    /// the asker, so a strict answer would be refused — and the cap would create exactly the
    /// deadlock it exists to prevent. This is the broker's escape hatch and should not be
    /// called on the ordinary path.
    /// </remarks>
    public TransitionResult ForceUnknown(DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        State = ExchangeState.Closed;
        ClosedUtc = nowUtc;
        OwedBy = Answerer;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Marks the exchange orphaned after a time-out or a participant disconnect.
    /// </summary>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <returns>
    /// <see cref="TransitionResult.Ok"/> on success, or a failure carrying
    /// <see cref="TransitionError.ExchangeNotOpen"/>.
    /// </returns>
    /// <remarks>
    /// Orphaning releases the pair slot and removes the wait-for edge, so an agent that
    /// vanishes cannot block its peers indefinitely — while preserving the question so it is
    /// not silently lost. Call <see cref="Resume"/> when the owing agent returns.
    /// </remarks>
    public TransitionResult Orphan(DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        State = ExchangeState.Orphaned;
        ExpiresUtc = nowUtc;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Reopens an orphaned exchange, granting a fresh deadline.
    /// </summary>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <returns>
    /// <see cref="TransitionResult.Ok"/> on success, or a failure carrying
    /// <see cref="TransitionError.ExchangeNotOrphaned"/>.
    /// </returns>
    /// <remarks>
    /// <see cref="ClarifyDepth"/> is deliberately preserved. A restart is not a fresh budget
    /// for going in circles, and resetting it would let a pathological exchange evade the cap
    /// by cycling through orphan and resume.
    /// </remarks>
    public TransitionResult Resume(DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Orphaned)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOrphaned);
        }

        State = ExchangeState.Open;
        ExpiresUtc = nowUtc + Ttl;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Reconstructs an exchange from stored values without replaying its transitions.
    /// </summary>
    /// <param name="id">The storage identity.</param>
    /// <param name="asker">The asking agent.</param>
    /// <param name="answerer">The answering agent.</param>
    /// <param name="owedBy">The participant who currently owes a response.</param>
    /// <param name="state">The lifecycle state.</param>
    /// <param name="clarifyDepth">Clarification requests spent so far.</param>
    /// <param name="clarifyDepthCap">The clarification request limit.</param>
    /// <param name="ttl">The inactivity window.</param>
    /// <param name="openedUtc">The creation timestamp.</param>
    /// <param name="expiresUtc">The current deadline.</param>
    /// <param name="closedUtc">The closing timestamp, if closed.</param>
    /// <param name="isDecision">Whether the answer was flagged for promotion.</param>
    /// <param name="version">The optimistic concurrency stamp held by the stored row.</param>
    /// <returns>The reconstructed exchange.</returns>
    /// <remarks>
    /// Intended for the persistence layer only. This bypasses every transition guard, so the
    /// values supplied are trusted to be internally consistent — which the schema's
    /// <c>CHECK</c> constraints are responsible for guaranteeing.
    /// </remarks>
    public static Exchange Rehydrate(
        long id,
        AgentName asker,
        AgentName answerer,
        AgentName owedBy,
        ExchangeState state,
        int clarifyDepth,
        int clarifyDepthCap,
        TimeSpan ttl,
        DateTimeOffset openedUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset? closedUtc,
        bool isDecision,
        long version = 0)
    {
        var exchange = new Exchange(id, asker, answerer, ttl, clarifyDepthCap, openedUtc)
        {
            OwedBy = owedBy,
            State = state,
            ClarifyDepth = clarifyDepth,
            ExpiresUtc = expiresUtc,
            ClosedUtc = closedUtc,
            IsDecision = isDecision,
            Version = version
        };

        return exchange;
    }
}
