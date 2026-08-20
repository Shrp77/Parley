using Parley.Core;

namespace Parley.Store;

/// <summary>
/// The reason an attempt to open an exchange was refused.
/// </summary>
public enum OpenRejection
{
    /// <summary>No refusal; the exchange was opened.</summary>
    None = 0,

    /// <summary>
    /// This asker already has the maximum open exchanges with this peer.
    /// </summary>
    /// <remarks>
    /// Volume, not repetition. See <see cref="OpenOutcome.Outstanding"/> for what the asker is
    /// already waiting on, which is frequently enough for it to conclude that this question is
    /// already covered.
    /// </remarks>
    PairSaturated,

    /// <summary>
    /// An identical question from this asker to this peer is already open.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="PairSaturated"/> because it calls for a different response:
    /// saturation means wait, duplication means the question has already been asked and the
    /// answer is coming. See <see cref="OpenOutcome.Outstanding"/> for the exchange carrying
    /// it.
    /// </remarks>
    DuplicateQuestion,

    /// <summary>
    /// Admitting the question would close a cycle in the wait-for graph, deadlocking every
    /// agent in it. See <see cref="OpenOutcome.CyclePath"/> for the agents involved.
    /// </summary>
    WouldDeadlock,

    /// <summary>
    /// The answerer is not being routed to: it could not be spawned, or it has been retired.
    /// </summary>
    /// <remarks>
    /// Usually a repository that was deleted, moved, or renamed while its agent was still
    /// registered. It also covers a <c>retired</c> agent, which SPEC.md §14 says is not routed
    /// to at all — the two are one refusal because the asker's correct response is identical,
    /// and splitting them would disclose whether a peer was deliberately withdrawn or merely
    /// broken, which is the operator's business rather than the asker's. This is the one
    /// refusal that hands back no work, because there is none to hand back — and saying so
    /// immediately is the point. Accepting the question instead
    /// would cost a full inactivity window before it orphaned unanswered, teaching the asker
    /// nothing and costing the same again on every retry. An asker seeing this should tell its
    /// principal, not try later.
    /// </remarks>
    AnswererUnreachable,

    /// <summary>
    /// The question names a conversation that has already concluded.
    /// </summary>
    /// <remarks>
    /// SPEC.md §5.0: a closed conversation stays closed. Closure is by consensus, so admitting
    /// this would let one agent reopen a discussion everybody agreed was over — defeating the
    /// poll, the decline veto and the silence rule at once. Like
    /// <see cref="AnswererUnreachable"/> this hands back no work, and for the same reason: there
    /// is none. What the asker should do is drop the identifier, or use a new one. A discussion
    /// worth resuming is a new discussion, and identifiers cost nothing.
    /// </remarks>
    ConversationClosed,

    /// <summary>
    /// The asker has itself been retired, and may not open new exchanges.
    /// </summary>
    /// <remarks>
    /// SPEC.md §14 says a retired agent is not routed to. Admitting its question would commit
    /// the broker to breaking that: the answer is a message addressed to the asker, so every
    /// exchange opened by a retired agent is a routing violation already scheduled. Refusing at
    /// the point of asking is what keeps the rule from being one the broker undertakes to break
    /// later.
    /// </remarks>
    /// <para>
    /// Exchanges opened before retirement are left to finish rather than stranded. Retirement
    /// is an operator's judgement about a repository, not a kill signal, and discarding work
    /// already in flight would lose answers that peers had gone to the trouble of producing.
    /// </para>
    AskerRetired
}

/// <summary>
/// The result of attempting to open an exchange, carrying either the new exchange or enough
/// context for the caller to act on the refusal.
/// </summary>
/// <remarks>
/// <para>
/// The constructor is private and the named factories below are the only way to build one.
/// A public constructor would accept combinations that contradict themselves — a refusal
/// carrying an exchange, or <see cref="Succeeded"/> reporting true with
/// <see cref="Exchange"/> null. Callers read <see cref="Succeeded"/> and then dereference
/// <see cref="Exchange"/> without a null check, so that pairing is an invariant they rely
/// on rather than a convention, and only the factories can establish it.
/// </para>
/// <para>
/// <see cref="OpenRejection.PairSaturated"/> and <see cref="OpenRejection.DuplicateQuestion"/>
/// are kept apart because they call for completely different responses. Saturation means the
/// asker is already waiting on as much as this peer will take, and the answer to one of those
/// may well make this question unnecessary. Duplication means the question is already in
/// flight, and the only sensible response is to wait for the answer that is coming. Neither
/// tells the model to retry later, which SPEC.md §7.3 refuses to do: instructing a language
/// model to remember something for later produces a token-burning loop or silent abandonment,
/// so every refusal hands back work instead.
/// </para>
/// <para>
/// When the asker is merely waiting, there is no work to hand back and the outcome reports
/// what it is waiting on instead.
/// </para>
/// </remarks>
public sealed record OpenOutcome
{
    private OpenOutcome(
        Exchange? exchange,
        OpenRejection rejection,
        IReadOnlyList<Exchange> outstanding,
        IReadOnlyList<AgentName> cyclePath)
    {
        Exchange = exchange;
        Rejection = rejection;
        Outstanding = outstanding;
        CyclePath = cyclePath;
    }

    /// <summary>
    /// Gets the newly opened exchange.
    /// </summary>
    /// <value>
    /// The exchange, or <see langword="null"/> if the attempt was refused. Non-null exactly
    /// when <see cref="Succeeded"/> is <see langword="true"/>.
    /// </value>
    public Exchange? Exchange { get; }

    /// <summary>
    /// Gets the reason the attempt was refused.
    /// </summary>
    /// <value>
    /// The refusal reason, or <see cref="OpenRejection.None"/> on success.
    /// </value>
    public OpenRejection Rejection { get; }

    /// <summary>
    /// Gets what this asker already has open with this peer.
    /// </summary>
    /// <value>
    /// On <see cref="OpenRejection.PairSaturated"/>, every open exchange from this asker to
    /// this answerer. On <see cref="OpenRejection.DuplicateQuestion"/>, the single exchange
    /// already carrying the question. Empty otherwise.
    /// </value>
    /// <remarks>
    /// This is what makes both refusals actionable rather than an instruction to try later,
    /// which SPEC.md §7.3 refuses to give. An asker handed the questions it is already waiting
    /// on can usually see that the one it was about to send is among them, or is about to be
    /// answered by one of them.
    /// </remarks>
    public IReadOnlyList<Exchange> Outstanding { get; }

    /// <summary>
    /// Gets the agents forming the cycle the question would have closed.
    /// </summary>
    /// <value>
    /// On <see cref="OpenRejection.WouldDeadlock"/>: the agents in the order they block one
    /// another, starting with the asker. Empty otherwise.
    /// </value>
    public IReadOnlyList<AgentName> CyclePath { get; }

    /// <summary>
    /// Gets a value indicating whether the exchange was opened.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="Exchange"/> is populated; otherwise
    /// <see langword="false"/>.
    /// </value>
    public bool Succeeded => Rejection == OpenRejection.None;

    /// <summary>
    /// Creates a successful outcome.
    /// </summary>
    /// <param name="exchange">The newly opened exchange.</param>
    /// <returns>An outcome carrying <paramref name="exchange"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> is <see langword="null"/>.</exception>
    public static OpenOutcome Opened(Exchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return new(exchange, OpenRejection.None, [], []);
    }

    /// <summary>
    /// Creates a refusal indicating this asker has reached its limit with this peer.
    /// </summary>
    /// <param name="outstanding">Everything the asker already has open with the answerer.</param>
    /// <returns>A refusal carrying what is already in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outstanding"/> is <see langword="null"/>.</exception>
    public static OpenOutcome PairSaturated(IReadOnlyList<Exchange> outstanding)
    {
        ArgumentNullException.ThrowIfNull(outstanding);
        return new(null, OpenRejection.PairSaturated, outstanding, []);
    }

    /// <summary>
    /// Creates a refusal indicating this question is already open with this peer.
    /// </summary>
    /// <param name="existing">The exchange already carrying the question.</param>
    /// <returns>A refusal carrying the exchange to wait on.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="existing"/> is <see langword="null"/>.</exception>
    public static OpenOutcome Duplicate(Exchange existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return new(null, OpenRejection.DuplicateQuestion, [existing], []);
    }

    /// <summary>
    /// Creates a refusal indicating the answerer cannot be reached.
    /// </summary>
    /// <returns>A refusal carrying no work, because there is none.</returns>
    /// <remarks>
    /// Every other refusal returns something the asker can act on — the debt it owes, the
    /// exchange it waits on, the cycle it would close. This one cannot, and that absence is
    /// itself the information: nothing is coming from this peer, so stop asking.
    /// </remarks>
    public static OpenOutcome Unreachable() =>
        new(null, OpenRejection.AnswererUnreachable, [], []);

    /// <summary>
    /// Creates a refusal indicating the named conversation has concluded.
    /// </summary>
    /// <returns>A refusal carrying no work, because there is none.</returns>
    /// <remarks>
    /// The refusal states its reason, which SPEC.md §5.0 requires: an asker told only that its
    /// question was refused would retry with the same identifier indefinitely, since nothing
    /// about waiting reopens a concluded discussion.
    /// </remarks>
    public static OpenOutcome ConversationClosed() =>
        new(null, OpenRejection.ConversationClosed, [], []);

    /// <summary>
    /// Creates a refusal indicating the asker has been retired.
    /// </summary>
    /// <returns>A refusal carrying no work, because there is none.</returns>
    /// <remarks>
    /// Distinct from <see cref="Unreachable"/>, which is about the peer, because the remedy is
    /// different: an unreachable answerer means stop asking <em>this peer</em>, and this means
    /// stop working. An agent seeing it should tell its principal that its registration has
    /// been withdrawn.
    /// </remarks>
    public static OpenOutcome AskerRetired() =>
        new(null, OpenRejection.AskerRetired, [], []);

    /// <summary>
    /// Creates a refusal indicating the question would deadlock.
    /// </summary>
    /// <param name="cyclePath">The agents forming the cycle, starting with the asker.</param>
    /// <returns>A refusal carrying the cycle path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cyclePath"/> is <see langword="null"/>.</exception>
    public static OpenOutcome Deadlock(IReadOnlyList<AgentName> cyclePath)
    {
        ArgumentNullException.ThrowIfNull(cyclePath);
        return new(null, OpenRejection.WouldDeadlock, [], cyclePath);
    }
}
