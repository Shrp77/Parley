namespace Parley.Core;

/// <summary>
/// One Request and its Result, including every clarification between them (SPEC.md §6).
/// </summary>
/// <remarks>
/// <para>
/// This aggregate owns only what is true of one exchange in isolation: whose turn it is, how many
/// clarifications have been asked for, whether it has been collected, and whether it is still open.
/// Which conversation it belongs to, whether it closes a cycle and whether it is a duplicate are
/// decided at admission, against every open exchange, and live in the store.
/// </para>
/// <para>
/// <b>Two states and no timer.</b> An exchange is open until its receiver sends a Result or the
/// conversation it belongs to is closed (§5.0). Nothing that happens to a process ends it, and there
/// is no deadline to expire it: the two thresholds it carries decide only when silence is flagged to
/// the person running the deployment, which changes nothing about the exchange itself.
/// </para>
/// <para>
/// The requester and receiver are fixed at creation and never swap. The turn, tracked by
/// <see cref="OwedBy"/>, does: a clarification from the receiver hands it to the requester, and the
/// requester's reply returns it. Only the receiver ever sends the Result.
/// </para>
/// <para>
/// Transitions report refusal through <see cref="TransitionResult"/> rather than throwing. A
/// participant speaking out of turn is a routine event in a concurrent system, not an exceptional
/// one. Exceptions are reserved for arguments that could never be valid, such as a participant
/// addressing itself.
/// </para>
/// </remarks>
public sealed class Exchange
{
    private Exchange(
        long id,
        AgentName requester,
        AgentName receiver,
        TimeSpan uncollectedAfter,
        TimeSpan stalledAfter,
        int clarifyDepthCap,
        DateTimeOffset openedUtc)
    {
        Id = id;
        Requester = requester;
        Receiver = receiver;
        PairKey = PairKey.For(requester, receiver);
        OwedBy = receiver;
        State = ExchangeState.Open;
        UncollectedAfter = uncollectedAfter;
        StalledAfter = stalledAfter;
        ClarifyDepthCap = clarifyDepthCap;
        OpenedUtc = openedUtc;
    }

    /// <summary>Gets the storage identity of this exchange.</summary>
    /// <value>The primary key assigned when the exchange was inserted.</value>
    public long Id { get; }

    /// <summary>Gets the unordered pair key for the two participants.</summary>
    /// <value>The key every rule about traffic between two participants is built on (§7.1).</value>
    public PairKey PairKey { get; }

    /// <summary>Gets the participant that sent the Request.</summary>
    /// <value>Fixed for the life of the exchange.</value>
    public AgentName Requester { get; }

    /// <summary>Gets the participant asked to do the work.</summary>
    /// <value>
    /// Fixed for the life of the exchange, and the only participant permitted to send the Result —
    /// even while a clarification has handed the turn to the requester.
    /// </value>
    public AgentName Receiver { get; }

    /// <summary>Gets the participant who must move next.</summary>
    /// <value>Initially the <see cref="Receiver"/>. Flips on each <see cref="Clarify"/>.</value>
    public AgentName OwedBy { get; private set; }

    /// <summary>Gets the current lifecycle state.</summary>
    /// <value>Open or closed.</value>
    public ExchangeState State { get; private set; }

    /// <summary>Gets the number of clarifications the receiver has asked for.</summary>
    /// <value>Replies from the requester are not counted; see <see cref="Clarify"/>.</value>
    public int ClarifyDepth { get; private set; }

    /// <summary>Gets the clarification limit for this exchange.</summary>
    /// <value>
    /// The number permitted before <see cref="Clarify"/> refuses the next one, or
    /// <see cref="ExchangePolicy.UnboundedClarifications"/> when none is refused, which is the
    /// default (§5.2). Nothing closes the exchange when a cap is reached.
    /// </value>
    public int ClarifyDepthCap { get; }

    /// <summary>Gets how long the Request may go uncollected before it is flagged.</summary>
    /// <value>The first stage of §6, measured from <see cref="OpenedUtc"/>.</value>
    /// <remarks>
    /// Stamped at admission rather than read from configuration, so a deployment changing its
    /// settings does not re-flag work already in flight.
    /// </remarks>
    public TimeSpan UncollectedAfter { get; }

    /// <summary>Gets how long the owing participant may go without a message before it is flagged.</summary>
    /// <value>The second stage of §6, measured from collection or from the latest message.</value>
    /// <remarks>
    /// A different number from <see cref="UncollectedAfter"/> because it describes a different
    /// fact. Nobody having picked a Request up is worth showing quickly; somebody working on it for
    /// hours is ordinary. One threshold short enough for the first would flag every agent doing real
    /// work, and one long enough for the second would hide a vanished agent all afternoon.
    /// </remarks>
    public TimeSpan StalledAfter { get; }

    /// <summary>Gets when the receiver first collected the Request.</summary>
    /// <value><see langword="null"/> until a collection hands it over.</value>
    public DateTimeOffset? DeliveredUtc { get; private set; }

    /// <summary>Gets how the exchange closed, once it has.</summary>
    /// <value><see langword="null"/> while open.</value>
    /// <remarks>
    /// The rolled-up outcome of §5.3: the least favourable reading of the Result. It outlives the
    /// message text retention prunes, so it is what §15 is computed from.
    /// </remarks>
    public ExchangeOutcome? Outcome { get; private set; }

    /// <summary>Gets the exchange whose Request settled this one, when it closed as settled.</summary>
    /// <value><see langword="null"/> unless <see cref="Outcome"/> is <see cref="ExchangeOutcome.Settled"/>.</value>
    /// <remarks>
    /// An exchange rather than a message, because retention deletes messages (§9.4) and the record of what settled a
    /// question must outlive them (SPEC.md §5.6).
    /// </remarks>
    public long? SettledBy { get; private set; }

    /// <summary>Gets the instant the exchange was opened.</summary>
    /// <value>The creation timestamp, in UTC.</value>
    public DateTimeOffset OpenedUtc { get; }

    /// <summary>Gets the instant the exchange was closed.</summary>
    /// <value>The closing timestamp, or <see langword="null"/> while open.</value>
    public DateTimeOffset? ClosedUtc { get; private set; }

    /// <summary>Gets the optimistic concurrency stamp last read from or written to storage.</summary>
    /// <value>Zero for an exchange that has never been saved.</value>
    /// <remarks>
    /// A persistence concern that lives here only because it has to round-trip with the aggregate.
    /// Two paths that both load, transition and save would otherwise silently lose one transition,
    /// and the one most likely to be lost is a <see cref="ClarifyDepth"/> increment.
    /// </remarks>
    public long Version { get; private set; }

    /// <summary>Records that this instance has been persisted, adopting the stamp the store assigned.</summary>
    /// <param name="version">The concurrency stamp now held by the stored row.</param>
    /// <remarks>
    /// Internal and reachable only through the grant to Parley.Store: adopting a stamp anywhere else
    /// silently defeats the concurrency check.
    /// </remarks>
    internal void MarkSaved(long version) => Version = version;

    /// <summary>Determines whether a participant is party to this exchange.</summary>
    /// <param name="agent">The participant to test.</param>
    /// <returns><see langword="true"/> if it is the requester or the receiver.</returns>
    public bool Involves(AgentName agent) => agent == Requester || agent == Receiver;

    /// <summary>Gets the participant waiting on <see cref="OwedBy"/>.</summary>
    /// <value>Whichever party does not owe the next move.</value>
    public AgentName Waiting => OwedBy == Receiver ? Requester : Receiver;

    /// <summary>Opens a new exchange, with the receiver owing the first move.</summary>
    /// <param name="id">The storage identity assigned to this exchange.</param>
    /// <param name="requester">The participant sending the Request.</param>
    /// <param name="receiver">The participant asked to do the work.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="policy">
    /// The terms to admit it under, or <see langword="null"/> for <see cref="ExchangePolicy.Default"/>.
    /// A resolved policy rather than the name of one: selection belongs to the operator (§7.1).
    /// </param>
    /// <returns>The newly opened exchange.</returns>
    /// <exception cref="ArgumentException">The requester and receiver are the same participant.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is out of range.</exception>
    public static Exchange Open(
        long id,
        AgentName requester,
        AgentName receiver,
        DateTimeOffset nowUtc,
        ExchangePolicy? policy = null)
    {
        if (requester == receiver)
        {
            throw new ArgumentException(
                $"A participant cannot send a request to itself ('{requester}').", nameof(receiver));
        }

        var terms = policy ?? ExchangePolicy.Default;
        terms.Validate();

        return new Exchange(
            id, requester, receiver, terms.UncollectedAfter, terms.StalledAfter,
            terms.ClarifyDepthCap, nowUtc);
    }

    /// <summary>Records that the receiver has collected the Request.</summary>
    /// <param name="nowUtc">The instant of collection, in UTC.</param>
    /// <returns><see cref="TransitionResult.Ok"/>, or a failure if the exchange is not open.</returns>
    /// <remarks>
    /// Only the first collection is recorded. A Request delivered again after a restart (§6) was
    /// collected before, and restamping it would hide how long it has really been owed.
    /// </remarks>
    public TransitionResult Deliver(DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        DeliveredUtc ??= nowUtc;
        return TransitionResult.Ok;
    }

    /// <summary>Sends a clarification, handing the turn to the other participant.</summary>
    /// <param name="from">The participant sending it. Must hold the turn.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <returns><see cref="TransitionResult.Ok"/>, or why the move was refused.</returns>
    /// <remarks>
    /// <para>
    /// Depth increments only when the <see cref="Receiver"/> sends. The requester's reply supplies
    /// what was asked for rather than opening a round, and charging both directions would halve any
    /// cap without saying so.
    /// </para>
    /// <para>
    /// When a cap is set and reached this refuses and nothing else happens (§5.2). The receiver sends
    /// a Result with what it has — <c>blocked</c> where that is the truth — and nothing closes the
    /// exchange on its behalf, because an outcome is a participant's judgement about its own work.
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

        var fromReceiver = from == Receiver;

        if (fromReceiver && ClarifyDepth >= ClarifyDepthCap)
        {
            return TransitionResult.Fail(TransitionError.ClarifyDepthExceeded);
        }

        if (fromReceiver)
        {
            ClarifyDepth++;
        }

        OwedBy = fromReceiver ? Requester : Receiver;
        return TransitionResult.Ok;
    }

    /// <summary>Closes the exchange with the receiver's Result.</summary>
    /// <param name="from">The participant sending the Result. Must be the receiver, holding the turn.</param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="outcome">
    /// The rolled-up outcome of the Result's items. <see cref="ExchangeOutcome.Closed"/> is refused:
    /// only the broker records that, when the conversation is closed first.
    /// </param>
    /// <returns><see cref="TransitionResult.Ok"/>, or why the move was refused.</returns>
    public TransitionResult Close(AgentName from, DateTimeOffset nowUtc, ExchangeOutcome outcome)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        if (!Involves(from))
        {
            return TransitionResult.Fail(TransitionError.NotAParticipant);
        }

        if (from != Receiver)
        {
            return TransitionResult.Fail(TransitionError.OnlyReceiverSendsResult);
        }

        if (from != OwedBy)
        {
            return TransitionResult.Fail(TransitionError.NotYourTurn);
        }

        if (outcome == ExchangeOutcome.Closed)
        {
            // The schema refuses this too. Refusing it here means the caller learns what it did
            // rather than meeting a trigger three layers down.
            return TransitionResult.Fail(TransitionError.ClosedIsTheBrokers);
        }

        State = ExchangeState.Closed;
        ClosedUtc = nowUtc;
        Outcome = outcome;
        OwedBy = Receiver;
        return TransitionResult.Ok;
    }

    /// <summary>Closes the requester's own question to the person, because something the person wrote later settled it.</summary>
    /// <param name="from">The participant settling it. Must be the requester, with the turn the receiver's.</param>
    /// <param name="settling">The exchange whose Request settled it.</param>
    /// <param name="nowUtc">The instant, which is also the instant of the reply that settles it.</param>
    /// <returns><see cref="TransitionResult.Ok"/>, or why the move was refused.</returns>
    /// <remarks>
    /// <para>
    /// <b>The one close a requester makes, and only of a question to the person</b> (SPEC.md §5.6). Whether the receiver
    /// is the person, and whether <paramref name="settling"/> is a Request the person opened with this requester in the
    /// same conversation, are facts about other rows: the broker checks them and storage checks them again. What is true
    /// of this exchange alone is checked here.
    /// </para>
    /// <para>
    /// No message is written on it. The person did not answer here and the requester wrote no Result here, so the
    /// outcome and the exchange that settled it are the whole record.
    /// </para>
    /// </remarks>
    public TransitionResult Settle(AgentName from, long settling, DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        if (from != Requester)
        {
            return TransitionResult.Fail(TransitionError.NotAParticipant);
        }

        if (OwedBy != Receiver)
        {
            return TransitionResult.Fail(TransitionError.NotYourTurn);
        }

        if (settling == Id)
        {
            throw new ArgumentException("An exchange cannot settle itself.", nameof(settling));
        }

        State = ExchangeState.Closed;
        ClosedUtc = nowUtc;
        Outcome = ExchangeOutcome.Settled;
        SettledBy = settling;
        return TransitionResult.Ok;
    }

    /// <summary>Hands the turn back to the receiver, on a request whose requester the receiver asked back.</summary>
    /// <param name="from">The participant taking the turn. Must be the receiver, with the turn the requester's.</param>
    /// <returns><see cref="TransitionResult.Ok"/>, or why the move was refused.</returns>
    /// <remarks>
    /// What the requester's own reply would have done (§5.2), taken by the receiver because the requester is the person
    /// and answered in a later Request instead (SPEC.md §5.6). Nothing else moves: no message, and the depth counts only
    /// what the receiver asks.
    /// </remarks>
    public TransitionResult TurnBack(AgentName from)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        if (from != Receiver)
        {
            return TransitionResult.Fail(TransitionError.NotAParticipant);
        }

        if (OwedBy != Requester)
        {
            return TransitionResult.Fail(TransitionError.NotYourTurn);
        }

        OwedBy = Receiver;
        return TransitionResult.Ok;
    }

    /// <summary>Closes the exchange because the conversation it belongs to closed first.</summary>
    /// <param name="nowUtc">The instant of closure, in UTC.</param>
    /// <returns><see cref="TransitionResult.Ok"/>, or a failure if the exchange is not open.</returns>
    /// <remarks>
    /// The one transition nobody party to the exchange makes. It is taken only together with the
    /// broker's own <c>closed</c> Result to each participant holding the exchange, in one write
    /// (§14), and the schema refuses the outcome without that Result.
    /// </remarks>
    public TransitionResult CloseWithConversation(DateTimeOffset nowUtc)
    {
        if (State != ExchangeState.Open)
        {
            return TransitionResult.Fail(TransitionError.ExchangeNotOpen);
        }

        State = ExchangeState.Closed;
        ClosedUtc = nowUtc;
        Outcome = ExchangeOutcome.Closed;
        return TransitionResult.Ok;
    }

    /// <summary>Reconstructs an exchange from stored values without replaying its transitions.</summary>
    /// <param name="id">The storage identity.</param>
    /// <param name="requester">The participant that sent the Request.</param>
    /// <param name="receiver">The participant asked to do the work.</param>
    /// <param name="owedBy">The participant who owes the next move.</param>
    /// <param name="state">The lifecycle state.</param>
    /// <param name="clarifyDepth">Clarifications the receiver has asked for.</param>
    /// <param name="clarifyDepthCap">The clarification limit.</param>
    /// <param name="uncollectedAfter">The first flag threshold.</param>
    /// <param name="stalledAfter">The second flag threshold.</param>
    /// <param name="openedUtc">The creation timestamp.</param>
    /// <param name="deliveredUtc">When the Request was first collected, if it has been.</param>
    /// <param name="closedUtc">The closing timestamp, if closed.</param>
    /// <param name="outcome">How it closed, or <see langword="null"/> while open.</param>
    /// <param name="version">The optimistic concurrency stamp held by the stored row.</param>
    /// <param name="settledBy">The exchange that settled it, when it closed as settled.</param>
    /// <returns>The reconstructed exchange.</returns>
    /// <remarks>
    /// For the persistence layer only. It bypasses every transition guard, so the values are trusted
    /// to be consistent — which the schema's constraints are responsible for guaranteeing.
    /// </remarks>
    public static Exchange Rehydrate(
        long id,
        AgentName requester,
        AgentName receiver,
        AgentName owedBy,
        ExchangeState state,
        int clarifyDepth,
        int clarifyDepthCap,
        TimeSpan uncollectedAfter,
        TimeSpan stalledAfter,
        DateTimeOffset openedUtc,
        DateTimeOffset? deliveredUtc,
        DateTimeOffset? closedUtc,
        ExchangeOutcome? outcome,
        long version = 0,
        long? settledBy = null) =>
        new(id, requester, receiver, uncollectedAfter, stalledAfter, clarifyDepthCap, openedUtc)
        {
            OwedBy = owedBy,
            State = state,
            ClarifyDepth = clarifyDepth,
            DeliveredUtc = deliveredUtc,
            ClosedUtc = closedUtc,
            Outcome = outcome,
            Version = version,
            SettledBy = settledBy
        };
}
