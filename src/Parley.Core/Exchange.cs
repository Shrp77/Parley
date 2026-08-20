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

    private Exchange(
        long id,
        AgentName asker,
        AgentName answerer,
        TimeSpan deliveryWindow,
        TimeSpan workingWindow,
        TimeSpan gracePeriod,
        int clarifyDepthCap,
        DateTimeOffset openedUtc)
    {
        Id = id;
        Asker = asker;
        Answerer = answerer;
        PairKey = PairKey.For(asker, answerer);
        OwedBy = answerer;
        State = ExchangeState.Open;
        DeliveryWindow = deliveryWindow;
        WorkingWindow = workingWindow;
        GracePeriod = gracePeriod;
        ClarifyDepthCap = clarifyDepthCap;
        OpenedUtc = openedUtc;

        // The delivery window runs first: nothing has picked the question up yet.
        ExpiresUtc = openedUtc + deliveryWindow;
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
    /// Gets the window allowed for the question to be picked up.
    /// </summary>
    /// <value>Short. Runs from the question being written until it is delivered.</value>
    /// <remarks>
    /// Silence here means nobody collected the question: the agent never spawned, or died on
    /// the way. That is the case worth reacting to quickly, because an exchange holding a
    /// wait-for edge blocks peers who have done nothing wrong.
    /// </remarks>
    public TimeSpan DeliveryWindow { get; }

    /// <summary>
    /// Gets the window allowed for an answer once the question has been delivered.
    /// </summary>
    /// <value>Long. Runs from delivery, and is renewed by each clarification.</value>
    /// <remarks>
    /// Silence here means something entirely different from silence before delivery: the agent
    /// was alive, took the question, and is working. Answering across a repository boundary can
    /// mean building something or running a test suite, so this is measured in hours. One timer
    /// cannot serve both cases — short enough to catch a dead agent will orphan a working one,
    /// and long enough to let real work finish leaves a vanished agent holding its edge all
    /// afternoon. Delivery is already recorded, so the split costs no extra message.
    /// </remarks>
    public TimeSpan WorkingWindow { get; }

    /// <summary>
    /// Gets how long an orphan is kept before it is ghosted.
    /// </summary>
    /// <value>The period after orphaning during which the owing agent may still return.</value>
    /// <remarks>
    /// Carried per exchange rather than read from configuration when the sweep runs, so an
    /// exchange is ghosted on the terms it was opened under. Held globally, lengthening the
    /// setting would retroactively revive orphans already past the old period, and shortening
    /// it would ghost a batch of live ones at once.
    /// </remarks>
    public TimeSpan GracePeriod { get; }

    /// <summary>
    /// Gets the instant the question was delivered, if it has been.
    /// </summary>
    /// <value><see langword="null"/> until an inbox drain collects it.</value>
    public DateTimeOffset? DeliveredUtc { get; private set; }

    /// <summary>
    /// Gets why the exchange closed, once it has.
    /// </summary>
    /// <value><see langword="null"/> while the exchange is open or orphaned.</value>
    /// <remarks>
    /// A rollup of the per-sub-question resolutions, taking the least resolved of them, so an
    /// exchange counts as answered only when every part of it was. It outlives the message text
    /// that retention eventually prunes, which makes it the only thing left distinguishing a
    /// ghost from an answer.
    /// </remarks>
    public Resolution? Resolution { get; private set; }

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
    /// <param name="policy">
    /// The terms to admit this exchange under, or <see langword="null"/> for
    /// <see cref="ExchangePolicy.Default"/>. A resolved policy rather than the name of one:
    /// selection belongs to the operator, and no parameter here should let an agent ask for a
    /// longer window or a larger cap.
    /// </param>
    /// <returns>The newly opened exchange.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="asker"/> and <paramref name="answerer"/> are the same agent.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> carries a value outside what storage will accept.
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
        ExchangePolicy? policy = null)
    {
        if (asker == answerer)
        {
            throw new ArgumentException($"An agent cannot question itself ('{asker}').", nameof(answerer));
        }

        var terms = policy ?? ExchangePolicy.Default;
        terms.Validate();

        return new Exchange(
            id, asker, answerer, terms.DeliveryWindow, terms.WorkingWindow, terms.GracePeriod,
            terms.ClarifyDepthCap, nowUtc);
    }

    /// <summary>
    /// Records that the question has been collected, and starts the working window.
    /// </summary>
    /// <param name="nowUtc">The instant of delivery, in UTC.</param>
    /// <returns>
    /// <see cref="TransitionResult.Ok"/> on success, or a failure carrying
    /// <see cref="TransitionError.ExchangeNotOpen"/>.
    /// </returns>
    /// <remarks>
    /// Idempotent in effect but not silent: delivering twice renews the working window, which
    /// is what a replay after an orphan is supposed to do. What it must not do is run before
    /// the exchange is open, since a closed exchange has nothing left to deliver.
    /// </remarks>
    public TransitionResult Deliver(DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        DeliveredUtc = nowUtc;
        ExpiresUtc = nowUtc + WorkingWindow;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Closes an orphaned exchange that nobody came back for.
    /// </summary>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <returns>
    /// <see cref="TransitionResult.Ok"/> on success, or a failure carrying
    /// <see cref="TransitionError.ExchangeNotOrphaned"/>.
    /// </returns>
    /// <remarks>
    /// The terminal state an orphan otherwise lacks. Without it a `never` resume policy retains
    /// orphans nothing ever clears, so the retention age cap is not in fact absolute — the gap
    /// SPEC.md §9.4 used to concede. Ghosting does not discard the question: the broker returns
    /// it to the asker along with the consequences the asker itself wrote down.
    /// </remarks>
    public TransitionResult Ghost(DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Orphaned)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOrphaned);
        }

        State = ExchangeState.Closed;
        ClosedUtc = nowUtc;
        Resolution = Core.Resolution.Ghosted;
        OwedBy = Answerer;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Reports whether this orphan's grace period has elapsed.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate against.</param>
    /// <param name="brokerStartUtc">When the broker most recently started.</param>
    /// <returns><see langword="true"/> if the exchange may be ghosted.</returns>
    /// <remarks>
    /// The clock runs from the later of orphaning and broker start. A broker that was down
    /// overnight returns to exchanges whose grace period elapsed while nothing could possibly
    /// have resumed them, and a plain wall-clock rule would ghost every one of them within a
    /// second of start-up. In steady running the two agree, because broker start is in the past
    /// for everything orphaned since.
    /// </remarks>
    public bool IsGhostableAt(DateTimeOffset nowUtc, DateTimeOffset brokerStartUtc)
    {
        if (State != ExchangeState.Orphaned)
        {
            return false;
        }

        var from = ExpiresUtc > brokerStartUtc ? ExpiresUtc : brokerStartUtc;
        return nowUtc >= from + GracePeriod;
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
        ExpiresUtc = nowUtc + WorkingWindow;
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Closes the exchange with an answer, releasing the pair slot.
    /// </summary>
    /// <param name="from">
    /// The agent answering. Must be the <see cref="Answerer"/> and must hold the turn.
    /// </param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="resolution">
    /// The rolled-up outcome, taking the least resolved of the per-sub-question resolutions.
    /// <see cref="Core.Resolution.Ghosted"/> is refused: only the broker records that.
    /// </param>
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
    public TransitionResult Answer(
        AgentName from,
        DateTimeOffset nowUtc,
        Resolution resolution = Core.Resolution.Answered,
        bool isDecision = false)
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

        if (resolution == Core.Resolution.Ghosted)
        {
            // A ghost asserts nobody answered, so it cannot arrive on an answer. The schema
            // refuses this too; refusing it here means the caller learns why rather than
            // meeting a constraint violation from three layers down.
            return TransitionResult.Fail(TransitionError.GhostIsNotAnAnswer);
        }

        State = ExchangeState.Closed;
        ClosedUtc = nowUtc;
        IsDecision = isDecision;
        Resolution = resolution;
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
        Resolution = Core.Resolution.Unknown;
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

        // The delivery window, not the working one. A replay marks the question undelivered
        // again (SPEC.md 14), so the exchange is back to waiting for someone to pick it up,
        // and granting hours for that would hide an agent that never returned at all.
        DeliveredUtc = null;
        ExpiresUtc = nowUtc + DeliveryWindow;
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
    /// <param name="deliveryWindow">The window allowed for pickup.</param>
    /// <param name="workingWindow">The window allowed for an answer once delivered.</param>
    /// <param name="gracePeriod">How long an orphan is kept before it is ghosted.</param>
    /// <param name="openedUtc">The creation timestamp.</param>
    /// <param name="expiresUtc">The current deadline.</param>
    /// <param name="closedUtc">The closing timestamp, if closed.</param>
    /// <param name="deliveredUtc">When the question was collected, if it has been.</param>
    /// <param name="isDecision">Whether the answer was flagged for promotion.</param>
    /// <param name="resolution">Why the exchange closed, or <see langword="null"/> if open.</param>
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
        TimeSpan deliveryWindow,
        TimeSpan workingWindow,
        TimeSpan gracePeriod,
        DateTimeOffset openedUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset? closedUtc,
        DateTimeOffset? deliveredUtc,
        bool isDecision,
        Resolution? resolution,
        long version = 0)
    {
        var exchange = new Exchange(
            id, asker, answerer, deliveryWindow, workingWindow, gracePeriod, clarifyDepthCap, openedUtc)
        {
            OwedBy = owedBy,
            State = state,
            ClarifyDepth = clarifyDepth,
            ExpiresUtc = expiresUtc,
            ClosedUtc = closedUtc,
            DeliveredUtc = deliveredUtc,
            IsDecision = isDecision,
            Resolution = resolution,
            Version = version
        };

        return exchange;
    }
}
