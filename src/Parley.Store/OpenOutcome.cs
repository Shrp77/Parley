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
    /// The pair already holds an open exchange.
    /// </summary>
    /// <remarks>
    /// Inspect <see cref="OpenOutcome.ReciprocalDebt"/> and
    /// <see cref="OpenOutcome.Blocking"/> to distinguish the two situations this covers:
    /// owing the peer an answer, versus already waiting on one.
    /// </remarks>
    PairBusy,

    /// <summary>
    /// Admitting the question would close a cycle in the wait-for graph, deadlocking every
    /// agent in it. See <see cref="OpenOutcome.CyclePath"/> for the agents involved.
    /// </summary>
    WouldDeadlock,

    /// <summary>
    /// The answerer could not be spawned and is not being routed to.
    /// </summary>
    /// <remarks>
    /// Usually a repository that was deleted, moved, or renamed while its agent was still
    /// registered. This is the one refusal that hands back no work, because there is none to
    /// hand back — and saying so immediately is the point. Accepting the question instead
    /// would cost a full inactivity window before it orphaned unanswered, teaching the asker
    /// nothing and costing the same again on every retry. An asker seeing this should tell its
    /// principal, not try later.
    /// </remarks>
    AnswererUnreachable
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
/// The two flavours of <see cref="OpenRejection.PairBusy"/> matter because they call for
/// completely different responses. When the asker owes a debt, the broker returns the
/// pending question rather than a retry instruction: instructing a language model to try
/// again later produces either a token-burning loop or silent abandonment, whereas handing
/// back the question it already owes converts a block into forward progress — and the answer
/// frequently makes the intended question unnecessary or reshapes it.
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
        Exchange? reciprocalDebt,
        Exchange? blocking,
        IReadOnlyList<AgentName> cyclePath)
    {
        Exchange = exchange;
        Rejection = rejection;
        ReciprocalDebt = reciprocalDebt;
        Blocking = blocking;
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
    /// Gets the exchange the asker must resolve before it may ask.
    /// </summary>
    /// <value>
    /// On <see cref="OpenRejection.PairBusy"/> where the asker is the party that owes a
    /// response: the exchange it must resolve first. Otherwise <see langword="null"/>.
    /// </value>
    public Exchange? ReciprocalDebt { get; }

    /// <summary>
    /// Gets the exchange the asker is already waiting on.
    /// </summary>
    /// <value>
    /// On <see cref="OpenRejection.PairBusy"/> where the asker is already waiting on this
    /// peer: the exchange it is waiting on. Otherwise <see langword="null"/>.
    /// </value>
    public Exchange? Blocking { get; }

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
        return new(exchange, OpenRejection.None, null, null, []);
    }

    /// <summary>
    /// Creates a refusal indicating the asker must answer the peer before it may ask.
    /// </summary>
    /// <param name="debt">The exchange the asker owes a response to.</param>
    /// <returns>A refusal carrying the outstanding debt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="debt"/> is <see langword="null"/>.</exception>
    public static OpenOutcome OweFirst(Exchange debt)
    {
        ArgumentNullException.ThrowIfNull(debt);
        return new(null, OpenRejection.PairBusy, debt, null, []);
    }

    /// <summary>
    /// Creates a refusal indicating the asker already has a question outstanding with this
    /// peer.
    /// </summary>
    /// <param name="blocking">The exchange the asker is currently waiting on.</param>
    /// <returns>A refusal carrying the blocking exchange.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="blocking"/> is <see langword="null"/>.</exception>
    public static OpenOutcome AlreadyWaiting(Exchange blocking)
    {
        ArgumentNullException.ThrowIfNull(blocking);
        return new(null, OpenRejection.PairBusy, null, blocking, []);
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
        new(null, OpenRejection.AnswererUnreachable, null, null, []);

    /// <summary>
    /// Creates a refusal indicating the question would deadlock.
    /// </summary>
    /// <param name="cyclePath">The agents forming the cycle, starting with the asker.</param>
    /// <returns>A refusal carrying the cycle path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cyclePath"/> is <see langword="null"/>.</exception>
    public static OpenOutcome Deadlock(IReadOnlyList<AgentName> cyclePath)
    {
        ArgumentNullException.ThrowIfNull(cyclePath);
        return new(null, OpenRejection.WouldDeadlock, null, null, cyclePath);
    }
}
